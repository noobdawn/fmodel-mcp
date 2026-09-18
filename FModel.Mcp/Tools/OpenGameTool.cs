using System.ComponentModel;
using System.Text.Json.Serialization;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using FModel.Mcp.Indexing;
using FModel.Mcp.Runtime;
using FModel.Mcp.Session;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp.Tools;

public sealed record OpenGameResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("mountOk")] bool MountOk,
    [property: JsonPropertyName("problems")] IReadOnlyList<string> Problems,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("gameDisplayName")] string? GameDisplayName,
    [property: JsonPropertyName("mountedContainers")] int MountedContainers,
    [property: JsonPropertyName("unmountedContainers")] int UnmountedContainers,
    [property: JsonPropertyName("missingAesKeyGuids")] IReadOnlyList<string> MissingAesKeyGuids,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("mountSeconds")] double MountSeconds,
    [property: JsonPropertyName("resolved")] IReadOnlyDictionary<string, object?> Resolved,
    [property: JsonPropertyName("nativeLibraries")] IReadOnlyDictionary<string, object?> NativeLibraries,
    [property: JsonPropertyName("index")] IReadOnlyDictionary<string, object?> Index,
    [property: JsonPropertyName("nextSteps")] IReadOnlyList<string> NextSteps);

[McpServerToolType]
public static class OpenGameTool
{
    [McpServerTool(Name = "open_game")]
    [Description("""
        挂载一个 UE 游戏目录并启动后台索引，返回 sessionId 供后续所有工具使用。

        挂载是同步的（几秒），索引是后台的。本工具立即返回，用 index_status 查进度。
        索引就绪前 list_dirs / find_references(outgoing) / read_asset 已可用。

        强烈建议传 scope 限定范围（例如 ["Game/Content/Characters/"]），
        可把索引时间从十几分钟降到一分钟内。

        未显式传参时会尝试读取本机 FModel 的 AppSettings.json 获取 UE 版本 / AES / 贴图平台。
        返回值 resolved 字段标明每个参数的来源（explicit / fmodel_config / default）。
        务必检查返回值的 mountOk 与 problems —— 挂载失败时后续查询全是空结果。
        """)]
    public static CallToolResult OpenGame(
        SessionManager sessions,
        [Description("游戏 Paks 目录的绝对路径")] string gameDirectory,
        [Description("路径前缀白名单，只索引这些前缀下的资产。留空则全量索引。示例 [\"Game/Content/Characters/\"]")]
        string[]? scope = null,
        [Description("CUE4Parse 的 EGame 枚举名，例如 GAME_Snowbreak、GAME_UE5_5")] string? ueVersion = null,
        [Description("AES 主密钥，0x 开头的 64 位十六进制")] string? aesMainKey = null,
        [Description("贴图平台：DesktopMobile / XboxAndPlaystation4 / NintendoSwitch / Playstation5")]
        string? texturePlatform = null,
        [Description(".usmap 或 .jmap 映射文件路径。UE5 无版本化属性的游戏必需")] string? mappingsPath = null,
        [Description("是否同时构建引用图，默认 true。实测只多约 81 秒，事后单独构建要重扫全库约 9 分钟")]
        bool buildRefGraph = true,
        CancellationToken ct = default)
        => ToolResponse.Run("open_game", () => Execute(
            sessions, gameDirectory, scope, ueVersion, aesMainKey, texturePlatform, mappingsPath, buildRefGraph, ct));

    private static OpenGameResult Execute(
        SessionManager sessions, string gameDirectory, string[]? scope, string? ueVersion,
        string? aesMainKey, string? texturePlatform, string? mappingsPath, bool buildRefGraph, CancellationToken ct)
    {
        if (!Directory.Exists(gameDirectory))
            throw new DirectoryNotFoundException($"游戏目录不存在: {gameDirectory}");

        var natives = NativeLibraries.EnsureLoaded();
        var fmodel = FModelConfig.TryLoad();
        var fmDir = fmodel?.Find(gameDirectory);

        var (game, gameSource) = ResolveEnum(ueVersion, fmDir?.UeVersion, EGame.GAME_UE5_LATEST);
        var (platform, platformSource) = ResolveEnum(texturePlatform, fmDir?.TexturePlatform, ETexturePlatform.DesktopMobile);

        var aes = aesMainKey;
        var aesSource = "explicit";
        if (string.IsNullOrWhiteSpace(aes)) { aes = fmDir?.AesMainKey; aesSource = aes is null ? "none" : "fmodel_config"; }

        var maps = mappingsPath;
        var mapsSource = "explicit";
        if (string.IsNullOrWhiteSpace(maps)) { maps = fmDir?.MappingsFilePath; mapsSource = maps is null ? "none" : "fmodel_config"; }

        var mapsFp = GameSession.ComputeMappingsFingerprint(maps);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var provider = new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories,
            new VersionContainer(game, platform), StringComparer.OrdinalIgnoreCase)
        {
            ReadNaniteData = true
        };
        PropertyUtil.SearchPropertyInTemplate = true;

        provider.Initialize();

        if (!string.IsNullOrWhiteSpace(aes))
            provider.SubmitKey(new FGuid(0U), new FAesKey(aes));
        foreach (var (guid, key) in fmDir?.AesDynamicKeys ?? [])
        {
            try { provider.SubmitKey(new FGuid(guid), new FAesKey(key)); }
            catch (Exception e) when (e is ArgumentException or FormatException) { /* 跳过无效密钥 */ }
        }
        provider.PostMount();

        if (!string.IsNullOrWhiteSpace(maps) && File.Exists(maps))
            provider.MappingsContainer = LoadMappings(maps);

        // 游戏怪癖：某些游戏的动画用 ACL 插件压缩（引用 /ACLPlugin/...），但插件内容按
        // 引擎标准目录存放 —— 注册虚拟路径映射后 /ACLPlugin/xxx 才能解析到实际文件。
        // 否则 ACL 编解码器加载不了：CompressedDataStructure 为空 → 动画帧数未知且无法导出。
        // 已知受益：DragonSword（40% 动画）；实测 A/B：注册后 NumFrames 0 → 121。
        // 不覆盖游戏自带的同名映射（如晶核注册了 Seria/Plugins/ACLPlugin）。
        if (!provider.VirtualPaths.ContainsKey("ACLPlugin") &&
            provider.Files.Keys.Any(k => k.StartsWith("Engine/Plugins/Animation/ACLPlugin/Content/", StringComparison.OrdinalIgnoreCase)))
        {
            provider.VirtualPaths["ACLPlugin"] = "Engine/Plugins/Animation/ACLPlugin";
        }

        sw.Stop();

        var normalizedScope = (scope ?? [])
            .Select(s => s.Replace('\\', '/').TrimStart('/'))
            .Where(s => s.Length > 0)
            .ToList();

        var indexKey = GameSession.ComputeIndexKey(gameDirectory, normalizedScope);
        var indexDir = Path.Combine(SessionManager.IndexRoot, indexKey);
        Directory.CreateDirectory(indexDir);
        var dbPath = Path.Combine(indexDir, "index.db");
        var dbExisted = File.Exists(dbPath);
        var reused = dbExisted && IsIndexUsable(dbPath, mapsFp);

        var problems = Diagnose(provider, normalizedScope, aesSource, gameSource, game);
        var indexOptions = new IndexOptions(normalizedScope, buildRefGraph, Environment.ProcessorCount);

        var session = new GameSession
        {
            Id = $"s{Guid.NewGuid():N}"[..9],
            GameDirectory = gameDirectory,
            Provider = provider,
            IndexPath = dbPath,
            IndexDirectory = indexDir,
            Scope = normalizedScope,
            IndexOptions = indexOptions,
            MappingsFingerprint = mapsFp,
            ResolvedParameters = new Dictionary<string, object?>
            {
                ["ueVersion"] = new { value = game.ToString(), source = gameSource },
                ["texturePlatform"] = new { value = platform.ToString(), source = platformSource },
                ["aesMainKey"] = new { value = Mask(aes), source = aesSource },
                ["mappingsPath"] = new { value = maps, source = mapsSource },
                ["fmodelConfigPath"] = fmodel?.FilePath,
                ["fmodelConfigMatched"] = fmDir is not null
            }
        };

        if (!reused)
        {
            if (dbExisted)
            {
                var staleReason = ExplainStaleIndex(dbPath, mapsFp);
                if (staleReason is not null) problems.Add(staleReason);
                if (!TryDelete(dbPath))
                    problems.Add(
                        "[索引文件被占用] 既有索引删不掉 —— 可能有另一个 MCP 进程正在写同一个库。" +
                        "本次会在既有库上重写（已启用先删后插，不会产生重复行），但两个进程同时索引会互相拖慢。");
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indexer = new Indexer(provider, dbPath, indexOptions);
            session.Indexer = indexer;
            session.IndexCts = cts;
            session.IndexTask = Task.Run(async () =>
            {
                await indexer.RunAsync(cts.Token).ConfigureAwait(false);
                if (indexer.Progress.Phase is "done") FinalizeIndex(session);
            }, CancellationToken.None);
        }
        else
        {
            session.IndexReused = true;
            session.ReusedRowCount = CountRows(dbPath);
            session.Integrity = ReadIntegrity(dbPath);

            if (session.ReusedRowCount == 0)
                problems.Add(
                    "[空索引] 复用的既有索引里 0 行。上一次索引可能因 scope 写错或挂载失败而没索引到任何东西。" +
                    "先用 list_dirs 确认真实目录结构，再用正确的 scope 重新 open_game。");
        }

        sessions.Admit(session);

        var scopeHint = normalizedScope.Count == 0
            ? "未限定 scope，正在索引全部资产。大型游戏可能十几分钟 —— 若只关心部分目录，建议带 scope 重开。"
            : $"已限定 scope: {string.Join(", ", normalizedScope)}";

        return new OpenGameResult(
            session.Id,
            provider.Files.Count > 0 && provider.MountedVfs.Count > 0,
            problems,
            provider.ProjectName,
            provider.GameDisplayName,
            provider.MountedVfs.Count,
            provider.UnloadedVfs.Count,
            provider.RequiredKeys.Select(g => g.ToString()).ToList(),
            provider.Files.Count,
            Math.Round(sw.Elapsed.TotalSeconds, 2),
            session.ResolvedParameters,
            new Dictionary<string, object?>
            {
                ["oodle"] = natives.Oodle,
                ["zlib"] = natives.Zlib,
                ["detex"] = natives.Detex,
                ["acl"] = natives.Acl,
                ["notes"] = natives.Notes
            },
            new Dictionary<string, object?>
            {
                ["path"] = dbPath,
                ["reusedExisting"] = reused,
                ["buildRefGraph"] = buildRefGraph,
                ["status"] = reused ? "done" : "scanning",
                ["integrity"] = session.Integrity
            },
            [
                scopeHint,
                "下一步建议先调 describe_dataset 获取资产组织概览与索引 schema，再用 query_index 写 SQL。",
                reused ? "已复用磁盘上的既有索引。" : "索引正在后台构建，用 index_status 查进度。"
            ]);
    }

    /// <summary>挂载诊断 —— 空结果必须显式告警，否则 Agent 会拿着 0 行继续往下推理。</summary>
    private static List<string> Diagnose(
        AbstractFileProvider provider, List<string> scope, string aesSource, string gameSource, EGame game)
    {
        var problems = new List<string>();
        var vfs = provider as CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider;
        var mounted = vfs?.MountedVfs.Count ?? 0;
        var unloaded = vfs?.UnloadedVfs.Count ?? 0;
        var required = vfs?.RequiredKeys.Count ?? 0;

        if (mounted == 0 && unloaded > 0)
        {
            problems.Add(
                $"[挂载失败] {unloaded} 个容器一个都没挂上，缺 {required} 个 AES 密钥。" +
                (aesSource == "none"
                    ? " 没有拿到 AES 主密钥 —— 本机 FModel 配置里没有这个目录（你在 FModel 里用的可能是另一个安装路径），请显式传 aesMainKey。"
                    : " 提供的 AES 密钥无法解密这些容器，请确认密钥与游戏版本匹配。"));
        }
        else if (required > 0)
        {
            problems.Add($"[部分挂载] {required} 个容器因缺少 AES 密钥未挂载，这部分资产不可见。");
        }

        if (provider.Files.Count == 0)
            problems.Add("[空数据集] 挂载后文件数为 0，后续所有查询都会返回空。请先解决上面的问题。");

        if (gameSource == "default")
            problems.Add(
                $"[版本兜底] UE 版本用的是默认值 {game}，既没显式传入也没从 FModel 配置读到。" +
                "版本不对会导致解析出错或数据错乱，强烈建议显式传 ueVersion。");

        if (scope.Count > 0 && provider.Files.Count > 0)
        {
            var inScope = provider.Files.Keys.Count(k =>
                scope.Any(s => k.StartsWith(s, StringComparison.OrdinalIgnoreCase)));
            if (inScope == 0)
                problems.Add(
                    $"[scope 无匹配] scope 前缀下没有任何文件，索引会是空的。" +
                    "先用 list_dirs 确认真实目录结构（它查的是内存态全量文件，不受 scope 影响）。");
        }

        return problems;
    }

    private static ITypeMappingsProvider LoadMappings(string path) =>
        path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)
            ? new JmapTypeMappingsProvider(path)
            : new FileUsmapTypeMappingsProvider(path);

    private static (T Value, string Source) ResolveEnum<T>(string? explicitName, T? fromConfig, T fallback)
        where T : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            if (Enum.TryParse<T>(explicitName, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                return (parsed, "explicit");
            throw new ArgumentException(
                $"'{explicitName}' 不是有效的 {typeof(T).Name}。" +
                $"有效值示例: {string.Join(", ", Enum.GetNames<T>().Take(8))} ...（共 {Enum.GetNames<T>().Length} 个）");
        }
        if (fromConfig.HasValue) return (fromConfig.Value, "fmodel_config");
        return (fallback, "default");
    }

    private static string? Mask(string? key) =>
        string.IsNullOrEmpty(key) ? null : key.Length <= 10 ? "***" : $"{key[..6]}...{key[^4..]}";

    private static bool TryDelete(string path)
    {
        var ok = true;
        // 连同 WAL/SHM 一起清 —— 只删主库会把上次的预写日志留在原地
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(f)) continue;
            try { File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// 既有索引是否可直接复用：必须已完成，且建立时的映射指纹与本次一致。
    /// 缺映射记录（修复前建立的索引）只在当前也无映射时接受 —— 有映射就必须重建，否则会复用一份读不出属性的坏索引。
    /// </summary>
    private static bool IsIndexUsable(string dbPath, string mappingsFingerprint)
    {
        try
        {
            using var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            cn.Open();
            if (IndexSchema.GetMeta(cn, "status") != "done") return false;
            var recorded = IndexSchema.GetMeta(cn, "mappings_fingerprint");
            if (recorded is null) return mappingsFingerprint == "none";
            return string.Equals(recorded, mappingsFingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException) { return false; }
    }

    /// <summary>解释为什么不能复用既有索引（写进 problems，不让"重建"这件事静默发生）。</summary>
    private static string? ExplainStaleIndex(string dbPath, string mappingsFingerprint)
    {
        try
        {
            using var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            cn.Open();
            if (IndexSchema.GetMeta(cn, "status") != "done")
                return "[索引未完成] 上次索引被中断（或从未跑完），本次从头重建。";

            var recorded = IndexSchema.GetMeta(cn, "mappings_fingerprint");
            if (recorded is null && mappingsFingerprint != "none")
                return "[索引缺少映射标记] 既有索引建立时未记录映射指纹（旧版本行为），本次重建以纳入映射。";
            if (recorded is not null && !string.Equals(recorded, mappingsFingerprint, StringComparison.OrdinalIgnoreCase))
                return "[映射已更新] 映射文件与索引建立时不一致，本次重建。";
            return null;
        }
        catch (SqliteException) { return null; }
    }

    private static DatasetIntegrity? ReadIntegrity(string dbPath)
    {
        try
        {
            using var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            cn.Open();
            var status = IndexSchema.GetMeta(cn, "integrity_status");
            if (status is null) return null;
            return new DatasetIntegrity(
                status,
                int.TryParse(IndexSchema.GetMeta(cn, "integrity_mesh_total"), out var t) ? t : 0,
                int.TryParse(IndexSchema.GetMeta(cn, "integrity_mesh_suspect"), out var s) ? s : 0,
                double.TryParse(IndexSchema.GetMeta(cn, "integrity_rate"), out var r) ? r : 0d,
                IndexSchema.GetMeta(cn, "integrity_message") ?? "");
        }
        catch (SqliteException) { return null; }
    }

    private static int CountRows(string dbPath)
    {
        try
        {
            using var cn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM assets";
            return cmd.ExecuteScalar() is long v ? (int)v : 0;
        }
        catch (SqliteException) { return 0; }
    }

    internal static void FinalizeIndex(GameSession session)
    {
        try
        {
            using var cn = new SqliteConnection($"Data Source={session.IndexPath}");
            cn.Open();

            var total = 0;
            var suspect = 0;
            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*), SUM(CASE WHEN integrity <> 'ok' THEN 1 ELSE 0 END)
                    FROM assets WHERE class_name IN ('StaticMesh','SkeletalMesh')
                    """;
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    suspect = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                }
            }

            var integrity = Sanity.Judge(total, suspect);
            session.Integrity = integrity;

            IndexSchema.SetMeta(cn, "status", "done");
            IndexSchema.SetMeta(cn, "game_directory", session.GameDirectory);
            IndexSchema.SetMeta(cn, "scope", string.Join(",", session.Scope));
            IndexSchema.SetMeta(cn, "mappings_fingerprint", session.MappingsFingerprint);
            IndexSchema.SetMeta(cn, "built_at_utc", DateTime.UtcNow.ToString("O"));
            IndexSchema.SetMeta(cn, "integrity_status", integrity.Status);
            IndexSchema.SetMeta(cn, "integrity_mesh_total", integrity.MeshTotal.ToString());
            IndexSchema.SetMeta(cn, "integrity_mesh_suspect", integrity.MeshSuspect.ToString());
            IndexSchema.SetMeta(cn, "integrity_rate", integrity.SuspectRate.ToString("F4"));
            IndexSchema.SetMeta(cn, "integrity_message", integrity.Message);

            // 引用图为空时必须能解释原因：IoStore 包不走 FObjectImport，当前解析不了
            var skipped = session.Indexer?.IoStoreSkipped ?? 0;
            IndexSchema.SetMeta(cn, "ref_graph_iostore_skipped", skipped.ToString());
        }
        catch (SqliteException)
        {
            // 索引可用性不受完整性统计失败影响
        }
    }
}

[McpServerToolType]
public static class IndexStatusTool
{
    [McpServerTool(Name = "index_status")]
    [Description("查询后台索引进度。索引未完成时 query_index 只能查到已写入的部分。")]
    public static CallToolResult IndexStatus(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId)
        => ToolResponse.Run("index_status", () =>
        {
            var s = sessions.Require(sessionId);
            var p = s.Progress;
            var skipped = s.Indexer?.IoStoreSkipped ?? 0;
            return new
            {
                sessionId,
                phase = p.Phase,
                processed = p.Processed,
                total = p.Total,
                percentage = p.Percentage,
                suspectCount = p.SuspectCount,
                refEdges = p.RefEdges,
                ioStoreSkipped = skipped,
                refGraphWarning = skipped > 0 && p.RefEdges == 0
                    ? $"引用图为空：{skipped} 个包是 IoStore 格式（.utoc/.ucas），当前只支持传统 .pak 的 import 表。" +
                      "find_references(incoming) 与 expand_references 对这个游戏不可用。"
                    : null,
                elapsedSeconds = Math.Round(p.ElapsedSeconds, 1),
                error = p.Error,
                integrity = s.Integrity,
                ready = s.IndexReady
            };
        });
}
