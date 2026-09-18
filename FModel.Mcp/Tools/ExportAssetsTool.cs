using System.ComponentModel;
using System.Diagnostics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using FModel.Mcp.Indexing;
using FModel.Mcp.Runtime;
using FModel.Mcp.Session;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp.Tools;

[McpServerToolType]
public static class ExportAssetsTool
{
    [McpServerTool(Name = "export_assets")]
    [Description("""
        把资产导出成通用格式文件（模型 / 贴图 / 动画 / 材质），写到本地导出目录。

        支持的类型与格式：
          SkeletalMesh / StaticMesh  → UEFormat(.uemodel) | Gltf2(.glb) | ActorX(.psk/.pskx) | USD(.usda)
          AnimSequence / AnimMontage → UEFormat(.ueanim) | ActorX(.psa) | USD
          Texture2D 等               → Png | Jpeg | Tga | Webp
          MaterialInstance           → .json（+ 自动导出它引用的贴图）
          Skeleton / PoseAsset / World / Landscape 等也支持

        不在支持列表里的类型（DataTable、BlueprintGeneratedClass、PhysicsAsset…）会报 no_exporter，
        此时用 mode="jsonProperties" 可以把属性导成 JSON，或用 mode="raw" 导出原始字节。

        重要：
          - 导出**只写入受控的导出目录**，不会碰游戏目录。
          - 导出前会交叉核对索引的 integrity 列，suspect 资产会被提前警告（它们通常导出必失败）。
          - 先用 dryRun=true 探测哪些类型能导，再正式导。
        """)]
    public static CallToolResult ExportAssets(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("要导出的包路径数组（不含扩展名）。可先用 query_index 查出来")] string[] packagePaths,
        [Description("只导出这些名字的 export（可选）。留空则导出包内全部可导出对象")] string[]? objectNames = null,
        [Description("导出根下的子目录，例如 \"girl008b/meshes\"。不允许绝对路径或 ..")] string? outputSubdir = null,
        [Description("导出模式：auto=按类型自动选导出器 | raw=原始字节 | jsonProperties=属性 JSON")]
        string mode = "auto",
        [Description("只探测能否导出、不写任何文件。注意只能查出\"类型不支持\"，查不出运行期解析失败")]
        bool dryRun = false,
        [Description("网格格式：UEFormat | Gltf2 | ActorX | USD")] string meshFormat = "UEFormat",
        [Description("贴图格式：Png | Jpeg | Tga | Webp")] string textureFormat = "Png",
        [Description("Nanite 处理：NoNanite | NaniteOnly | NaniteFirst | NaniteLast")] string naniteMeshFormat = "NoNanite",
        [Description("LOD 取舍：Highest | Lowest | All")] string meshQuality = "Highest",
        [Description("骨骼插槽：Bone | Socket | None")] string socketFormat = "Bone",
        [Description("材质层级：TopLayerOnly | AllLayersNoRef | AllLayers")] string materialDepth = "TopLayerOnly",
        [Description("UEFormat 压缩：None | GZIP | ZSTD（仅 UEFormat 生效）")] string compressionFormat = "None",
        [Description("贴图质量 1-100（对 Jpeg/Webp 有效）")] int textureQuality = 100,
        [Description("导出网格时是否连带导出材质（材质会再自动带出它的贴图）")] bool exportMaterials = true,
        [Description("是否导出 MorphTarget")] bool exportMorphTargets = true,
        [Description("HDR 贴图是否保持 HDR")] bool exportHdrTexturesAsHdr = true,
        [Description("是否导出全部 mip 层级")] bool exportAllTextureMips = false,
        [Description("最多导出多少个包，默认 200，上限 2000")] int limit = 200)
        => ToolResponse.Run("export_assets", () => Execute(
            sessions, sessionId, packagePaths, objectNames, outputSubdir, mode, dryRun,
            meshFormat, textureFormat, naniteMeshFormat, meshQuality, socketFormat, materialDepth,
            compressionFormat, textureQuality, exportMaterials, exportMorphTargets,
            exportHdrTexturesAsHdr, exportAllTextureMips, limit));

    private static object Execute(
        SessionManager sessions, string sessionId, string[] packagePaths, string[]? objectNames,
        string? outputSubdir, string mode, bool dryRun,
        string meshFormat, string textureFormat, string naniteMeshFormat, string meshQuality,
        string socketFormat, string materialDepth, string compressionFormat, int textureQuality,
        bool exportMaterials, bool exportMorphTargets, bool exportHdrTexturesAsHdr,
        bool exportAllTextureMips, int limit)
    {
        var s = sessions.Require(sessionId);
        if (packagePaths is null || packagePaths.Length == 0)
            throw new ArgumentException("packagePaths 不能为空。可先用 query_index 查出要导的包，例如 " +
                                        "SELECT DISTINCT package_path FROM assets WHERE class_name='SkeletalMesh'");

        limit = Math.Clamp(limit, 1, 2000);
        var natives = NativeLibraries.EnsureLoaded();
        var paths = new PackagePathNormalizer(s.Provider);

        var wanted = packagePaths
            .Select(p => paths.Normalize(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        var nameFilter = objectNames is { Length: > 0 }
            ? new HashSet<string>(objectNames, StringComparer.OrdinalIgnoreCase)
            : null;

        // ── 导出前预警：索引里标了 suspect 的资产基本必然导出失败 ──
        var suspects = QuerySuspects(s, wanted);

        var options = BuildOptions(meshFormat, textureFormat, naniteMeshFormat, meshQuality,
            socketFormat, materialDepth, compressionFormat, textureQuality,
            exportMaterials, exportMorphTargets, exportHdrTexturesAsHdr, exportAllTextureMips,
            s.Provider.Versions.Platform);

        var session = new ExportSession { MaxDegreeOfParallelism = Environment.ProcessorCount };
        var queued = new List<object>();
        var skipped = new List<object>();
        var normalizedMode = mode.ToLowerInvariant();

        foreach (var pkgPath in wanted)
        {
            if (!FindReferencesTool.TryLoad(s, pkgPath, out var pkg))
            {
                skipped.Add(Diag(pkgPath, null, "not_found", "包不存在或无法加载",
                    "确认路径不含扩展名，且能在 query_index 的 package_path 列里查到。"));
                continue;
            }

            if (normalizedMode == "raw")
            {
                if (s.Provider.Files.TryGetValue(pkgPath + ".uasset", out var gf) ||
                    s.Provider.Files.TryGetValue(pkgPath + ".umap", out gf))
                {
                    if (!dryRun) session.Add(new RawDataExporter(gf, s.Provider));
                    queued.Add(new { packagePath = pkgPath, objectName = (string?)null, className = "RawData" });
                }
                else skipped.Add(Diag(pkgPath, null, "not_found", "找不到对应的 uasset/umap 文件", null));
                continue;
            }

            for (var i = 0; i < pkg.ExportMapLength; i++)
            {
                UObject obj;
                try { obj = pkg.ExportsLazy[i].Value; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    skipped.Add(Diag(pkgPath, null, Classify(e, natives), $"反序列化失败: {e.Message}", HintFor(e, natives)));
                    continue;
                }

                if (nameFilter is not null && !nameFilter.Contains(obj.Name)) continue;

                try
                {
                    if (normalizedMode == "jsonproperties")
                    {
                        if (!dryRun) session.Add(new JsonPropertiesExporter(obj));
                    }
                    else
                    {
                        // Add() 对不在分派表里的类型抛 NotSupportedException —— 这是唯一能提前查出的失败
                        if (dryRun) ProbeSupported(obj);
                        else session.Add(obj);
                    }
                    queued.Add(new { packagePath = pkgPath, objectName = obj.Name, className = obj.ExportType });
                }
                catch (NotSupportedException)
                {
                    skipped.Add(Diag(pkgPath, obj.Name, "no_exporter",
                        $"类型 '{obj.ExportType}' 没有对应的导出器",
                        "用 mode=\"jsonProperties\" 可导出它的属性 JSON，或 mode=\"raw\" 导出原始字节。"));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    skipped.Add(Diag(pkgPath, obj.Name, Classify(e, natives), e.Message, HintFor(e, natives)));
                }
            }
        }

        if (dryRun)
            return new
            {
                sessionId, dryRun = true,
                requestedPackages = wanted.Count,
                exportable = queued.Count,
                notExportable = skipped.Count,
                suspectWarnings = suspects,
                exportableItems = queued.Take(50).ToList(),
                blockedItems = skipped.Take(50).ToList(),
                note = "dryRun 只能查出\"类型不支持\"这一类问题。运行期的解析失败（数据不完整、压缩格式不支持）" +
                       "必须真正导一次才知道 —— 建议先对 1~2 个样本做正式导出探路。"
            };

        if (queued.Count == 0)
            return new
            {
                sessionId,
                exported = 0,
                failed = 0,
                skipped = skipped.Count,
                blockedItems = skipped.Take(50).ToList(),
                suspectWarnings = suspects,
                hint = "没有任何对象进入导出队列，全部被跳过。看 blockedItems 里的 kind 与 hint。"
            };

        var outDir = ExportPaths.Resolve(s.Provider.ProjectName, outputSubdir);
        var before = ExportPaths.Measure(outDir);

        var sw = Stopwatch.StartNew();
        IReadOnlyList<ExportResult> results;
        try
        {
            results = session.RunAsync(outDir, options, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"导出会话执行失败: {e.GetType().Name}: {e.Message}。" +
                $"原生库状态 Oodle={natives.Oodle} Zlib={natives.Zlib} ACL={natives.Acl}。", e);
        }
        sw.Stop();

        var ok = results.Where(r => r.Success).ToList();
        var bad = results.Where(r => !r.Success).ToList();
        var after = ExportPaths.Measure(outDir);
        var files = ok.SelectMany(r => r.DiskFilePaths ?? []).ToList();

        return new
        {
            sessionId,
            outputDirectory = outDir,
            elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            queued = queued.Count,
            exported = ok.Count,
            failed = bad.Count,
            skipped = skipped.Count,
            filesWritten = after.Files - before.Files,
            bytesWritten = after.Bytes - before.Bytes,
            byExtension = files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                .Select(g => new { ext = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).ToList(),
            sampleFiles = files.Take(25).ToList(),
            failures = bad.Take(30).Select(r => Diag(
                r.ObjectPath, null,
                Classify(r.Error, natives),
                r.Error?.Message ?? "未知错误",
                HintFor(r.Error, natives))).ToList(),
            blockedItems = skipped.Take(30).ToList(),
            suspectWarnings = suspects,
            nativeLibraries = new { natives.Oodle, natives.Zlib, natives.Detex, natives.Acl }
        };
    }

    /// <summary>dryRun 时复刻 ExportSession.Add 的类型分派判断，但不真正入队。</summary>
    private static void ProbeSupported(UObject obj)
    {
        var probe = new ExportSession();
        probe.Add(obj);   // 不支持时抛 NotSupportedException
        probe.Clear();
    }

    private static object Diag(string packagePath, string? objectName, string kind, string message, string? hint) =>
        new { packagePath, objectName, kind, message, hint };

    /// <summary>
    /// 失败归因。区分这四类对使用者意义完全不同：
    /// 数据不完整要换数据源，格式不支持要等上游，缺原生库自己就能修，没导出器换个 mode 就行。
    /// </summary>
    private static string Classify(Exception? e, NativeLibraryStatus natives)
    {
        if (e is null) return "unknown";
        if (e is NotSupportedException) return "no_exporter";

        var m = e.Message;
        if (m.Contains("has no LODs", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("index buffer", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("no valid bulk data", StringComparison.OrdinalIgnoreCase))
            return "dataset_incomplete";

        if (m.Contains("Unsupported compressed data type", StringComparison.OrdinalIgnoreCase))
            return natives.Acl ? "unsupported_format" : "native_missing";

        if (m.Contains("Failed to load skeleton", StringComparison.OrdinalIgnoreCase))
            return "missing_dependency";

        if (e is DllNotFoundException || m.Contains("oodle", StringComparison.OrdinalIgnoreCase))
            return "native_missing";

        return "unknown";
    }

    private static string? HintFor(Exception? e, NativeLibraryStatus natives) => Classify(e, natives) switch
    {
        "dataset_incomplete" =>
            "该资产的顶点/索引数据没读出来 —— 典型原因是 pak 集合不完整（例如只从完整安装里挑了部分 pak）。" +
            "用 query_index 查 integrity/integrity_note 确认，并考虑换用游戏的完整安装目录。",
        "unsupported_format" =>
            "CUE4Parse 不认识这个资产的压缩格式（常见于魔改引擎）。可以用 mode=\"raw\" 导出原始字节，" +
            "或用 mode=\"jsonProperties\" 拿到它的属性。",
        "native_missing" =>
            "缺原生库。ACL 压缩动画需要 CUE4Parse-Natives.dll，Oodle 压缩需要 oo2core_*.dll。" +
            "本服务器会自动复用 FModel 的 <Output>\\.data\\ 目录 —— 确认那里有这些 DLL。",
        "missing_dependency" =>
            "依赖的资产（如 Skeleton）不在当前挂载范围内。若用了 scope，先用 index_paths 或 " +
            "expand_references(autoIndex=true) 把依赖补进来。",
        "no_exporter" =>
            "换 mode=\"jsonProperties\"（属性 JSON）或 mode=\"raw\"（原始字节）。",
        _ => null
    };

    /// <summary>从索引里取这批包的 integrity 状态，导出前就把风险摆出来。</summary>
    private static List<object> QuerySuspects(GameSession s, List<string> paths)
    {
        var list = new List<object>();
        if (!File.Exists(s.IndexPath) || paths.Count == 0) return list;

        try
        {
            using var cn = new SqliteConnection($"Data Source={s.IndexPath};Mode=ReadOnly");
            cn.Open();
            const int Chunk = 400;
            for (var offset = 0; offset < paths.Count && list.Count < 30; offset += Chunk)
            {
                var slice = paths.Skip(offset).Take(Chunk).ToList();
                using var cmd = cn.CreateCommand();
                var names = slice.Select((_, i) => $"$p{i}").ToList();
                cmd.CommandText =
                    $"SELECT package_path, object_name, class_name, integrity_note FROM assets " +
                    $"WHERE integrity <> 'ok' AND package_path IN ({string.Join(",", names)}) LIMIT 30";
                for (var i = 0; i < slice.Count; i++) cmd.Parameters.AddWithValue(names[i], slice[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new
                    {
                        packagePath = r.GetString(0),
                        objectName = r.GetString(1),
                        className = r.GetString(2),
                        note = r.IsDBNull(3) ? null : r.GetString(3),
                        warning = "索引标记为 suspect —— 导出很可能失败。"
                    });
            }
        }
        catch (SqliteException) { /* 索引不可用不影响导出 */ }
        return list;
    }

    private static ExportOptions BuildOptions(
        string meshFormat, string textureFormat, string naniteMeshFormat, string meshQuality,
        string socketFormat, string materialDepth, string compressionFormat, int textureQuality,
        bool exportMaterials, bool exportMorphTargets, bool exportHdrTexturesAsHdr,
        bool exportAllTextureMips, ETexturePlatform platform)
        => new(
            meshFormat: ParseEnum<EMeshFormat>(meshFormat, nameof(meshFormat)),
            naniteMeshFormat: ParseEnum<ENaniteMeshFormat>(naniteMeshFormat, nameof(naniteMeshFormat)),
            meshQuality: ParseEnum<EMeshQuality>(meshQuality, nameof(meshQuality)),
            texturePlatform: platform,
            textureFormat: ParseEnum<ETextureFormat>(textureFormat, nameof(textureFormat)),
            textureQuality: Math.Clamp(textureQuality, 1, 100),
            exportHdrTexturesAsHdr: exportHdrTexturesAsHdr,
            exportAllTextureMips: exportAllTextureMips,
            materialDepth: ParseEnum<EMaterialDepth>(materialDepth, nameof(materialDepth)),
            exportMaterials: exportMaterials,
            exportMorphTargets: exportMorphTargets,
            socketFormat: ParseEnum<ESocketFormat>(socketFormat, nameof(socketFormat)),
            compressionFormat: ParseEnum<EFileCompressionFormat>(compressionFormat, nameof(compressionFormat)));

    private static T ParseEnum<T>(string value, string paramName) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
        throw new ArgumentException(
            $"{paramName} 的值 '{value}' 无效。可选：{string.Join(" | ", Enum.GetNames<T>())}");
    }
}
