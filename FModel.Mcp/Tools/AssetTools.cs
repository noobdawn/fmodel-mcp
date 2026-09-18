using System.ComponentModel;
using CUE4Parse.UE4.Assets;
using FModel.Mcp.Indexing;
using FModel.Mcp.Session;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace FModel.Mcp.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool(Name = "find_references")]
    [Description("""
        查询包级引用关系。

        outgoing（这个包引用了谁）—— 实时解析该包的 import 表，毫秒级，**不依赖索引**，任何时候可用。
        incoming（谁引用了这个包）—— 需要引用图，来自索引（open_game 的 buildRefGraph=true 时构建）。

        典型用法：一个角色的材质和贴图常常不在它自己的目录下，
        用 outgoing 从角色 mesh 出发展开，就能找全它实际用到的材质与贴图。
        """)]
    public static CallToolResult FindReferences(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("包路径，不含扩展名，例如 Game/Content/Characters/Girl/girl009b/girl009b_body01_skm")]
        string packagePath,
        [Description("outgoing = 该包引用了谁（实时）；incoming = 谁引用了该包（需引用图）")]
        string direction = "outgoing",
        [Description("outgoing 时沿引用链展开的层数，默认 1，最大 4")] int maxDepth = 1,
        [Description("最多返回多少条，默认 300")] int limit = 300)
        => ToolResponse.Run("find_references", () => Execute(sessions, sessionId, packagePath, direction, maxDepth, limit));

    private static object Execute(
        SessionManager sessions, string sessionId, string packagePath, string direction, int maxDepth, int limit)
    {
        var s = sessions.Require(sessionId);
        maxDepth = Math.Clamp(maxDepth, 1, 4);
        limit = Math.Clamp(limit, 1, 3000);

        // 入参既可能是虚拟路径 /Game/Characters/... 也可能是索引里的 Game/Content/Characters/...
        // 统一规范化成后者，两种写法都能用
        var paths = new PackagePathNormalizer(s.Provider);
        var normalized = paths.Normalize(packagePath);

        return direction.ToLowerInvariant() switch
        {
            "outgoing" => Outgoing(s, paths, normalized, maxDepth, limit),
            "incoming" => Incoming(s, normalized, limit),
            _ => throw new ArgumentException($"direction 只能是 outgoing 或 incoming，收到 '{direction}'。")
        };
    }

    private static object Outgoing(GameSession s, PackagePathNormalizer paths, string root, int maxDepth, int limit)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var results = new List<object>();
        var frontier = new List<string> { root };
        var unresolved = new List<string>();
        var ioStoreSkipped = 0;

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && results.Count < limit; depth++)
        {
            var next = new List<string>();
            foreach (var pkgPath in frontier)
            {
                if (!TryLoad(s, pkgPath, out var pkg))
                {
                    if (pkgPath != root) unresolved.Add(pkgPath);
                    continue;
                }

                // 只读 ImportMap 字段 —— 比走 ResolvedObject 快 18 倍（实测 1.0ms vs 18.2ms/包）
                // IoStore 的 IoPackage 用 FPackageObjectIndex 而非 FObjectImport，当前不支持
                if (pkg is not Package legacy) { ioStoreSkipped++; continue; }

                foreach (var import in legacy.ImportMap)
                {
                    if (!import.ClassName.Text.Equals("Package", StringComparison.Ordinal)) continue;
                    var raw = import.ObjectName.Text;
                    if (string.IsNullOrEmpty(raw)) continue;

                    // import 表存的是虚拟路径，规范化后才能直接喂回 query_index / read_asset
                    var dst = paths.Normalize(raw);
                    if (string.IsNullOrEmpty(dst) || !visited.Add(dst)) continue;

                    results.Add(new { path = dst, virtualPath = raw, depth, via = pkgPath });
                    next.Add(dst);
                    if (results.Count >= limit) break;
                }
                if (results.Count >= limit) break;
            }
            frontier = next;
        }

        return new
        {
            sessionId = s.Id,
            root,
            direction = "outgoing",
            mode = "realtime",
            maxDepth,
            count = results.Count,
            truncated = results.Count >= limit,
            unresolvedPackages = unresolved.Take(20).ToList(),
            ioStoreSkipped,
            note = "path 已规范化为索引里的 package_path 形式，可直接用于 query_index / read_asset；virtualPath 是包内原始的 UE 虚拟路径。",
            warning = IoStoreWarning(ioStoreSkipped, results.Count),
            references = results
        };
    }

    /// <summary>
    /// IoStore 包不走 FObjectImport，当前实现拿不到引用。
    /// 必须显式告警 —— 否则调用方会把"0 条引用"误读成"这个资产没有依赖"。
    /// </summary>
    internal static string? IoStoreWarning(int skipped, int found) =>
        skipped == 0
            ? null
            : $"有 {skipped} 个包是 IoStore 格式（.utoc/.ucas），当前实现只支持传统 .pak 的 import 表，" +
              $"这些包的引用未被解析。" +
              (found == 0
                  ? "本次结果为空**不代表该资产没有依赖**，而是无法解析。"
                  : "结果不完整。");

    private static object Incoming(GameSession s, string root, int limit)
    {
        if (!File.Exists(s.IndexPath))
            throw new InvalidOperationException(
                "反向引用需要引用图，但索引尚未创建。请先等待 index_status 显示 done。");

        if (!s.IndexReady)
            throw new InvalidOperationException(
                $"反向引用需要完整的引用图，当前索引进度 {s.Progress.Percentage:P0}。" +
                "反向查询在索引未完成时会漏结果，请等 index_status 显示 done 后重试。");

        using var cn = new SqliteConnection($"Data Source={s.IndexPath};Mode=ReadOnly");
        cn.Open();

        var hasGraph = HasRefRows(cn);
        if (!hasGraph)
            throw new InvalidOperationException(
                "索引里没有引用图 —— 本会话 open_game 时 buildRefGraph=false，或该游戏是 IoStore 格式（当前切片仅支持 .pak 的 import 表）。" +
                "请用 buildRefGraph=true 重新 open_game。");

        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT src_path FROM refs WHERE dst_path = $d LIMIT $n";
        cmd.Parameters.AddWithValue("$d", root);
        cmd.Parameters.AddWithValue("$n", limit);

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));

        return new
        {
            sessionId = s.Id,
            root,
            direction = "incoming",
            mode = "index",
            count = list.Count,
            truncated = list.Count >= limit,
            references = list
        };
    }

    private static bool HasRefRows(SqliteConnection cn)
    {
        using var c = cn.CreateCommand();
        c.CommandText = "SELECT EXISTS(SELECT 1 FROM refs LIMIT 1)";
        return c.ExecuteScalar() is long v && v == 1;
    }

    internal static bool TryLoad(GameSession s, string packagePath, out IPackage package)
    {
        foreach (var ext in new[] { ".uasset", ".umap" })
        {
            if (!s.Provider.Files.TryGetValue(packagePath + ext, out var file)) continue;
            try
            {
                package = s.Provider.LoadPackage(file);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                package = null!;
                return false;
            }
        }
        package = null!;
        return false;
    }
}

[McpServerToolType]
public static class ReadAssetTool
{
    private const int DefaultMaxBytes = 200_000;

    [McpServerTool(Name = "read_asset")]
    [Description("""
        把一个资产反序列化成 JSON 返回它的原始属性。

        用于索引列覆盖不到的信息 —— 例如读 DataTable / BlueprintGeneratedClass 拿到角色装配定义、
        读 MaterialInstance 看参数、读任意类型的完整属性。

        大包会被截断：优先用 objectName 或 exportIndex 精确定位到单个 export，
        不要一次性拉整个包。
        """)]
    public static CallToolResult ReadAsset(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("包路径，不含扩展名，例如 Game/Content/Characters/Girl/girl009b/girl009b_body01_skm")]
        string packagePath,
        [Description("只返回这个名字的 export（可选）")] string? objectName = null,
        [Description("只返回这个下标的 export（可选，与 objectName 二选一）")] int? exportIndex = null,
        [Description("JSON 截断阈值（字节），默认 200000")] int maxBytes = DefaultMaxBytes)
        => ToolResponse.Run("read_asset", () => Execute(sessions, sessionId, packagePath, objectName, exportIndex, maxBytes));

    private static object Execute(
        SessionManager sessions, string sessionId, string packagePath,
        string? objectName, int? exportIndex, int maxBytes)
    {
        var s = sessions.Require(sessionId);
        maxBytes = Math.Clamp(maxBytes, 1_000, 2_000_000);

        // 与 find_references 一致：虚拟路径 /Game/... 和索引路径 Game/Content/... 两种写法都接受
        var normalized = new PackagePathNormalizer(s.Provider).Normalize(packagePath);

        if (!FindReferencesTool.TryLoad(s, normalized, out var pkg))
            throw new FileNotFoundException(
                $"找不到或无法加载包 '{normalized}'（原始入参 '{packagePath}'）。" +
                "请确认路径不含扩展名，且能在 query_index 的 package_path 列里查到。");

        object payload;
        string selection;

        if (exportIndex is { } idx)
        {
            if (idx < 0 || idx >= pkg.ExportMapLength)
                throw new ArgumentOutOfRangeException(nameof(exportIndex),
                    $"exportIndex 越界：该包有 {pkg.ExportMapLength} 个 export（0..{pkg.ExportMapLength - 1}）。");
            payload = pkg.ExportsLazy[idx].Value;
            selection = $"exportIndex={idx}";
        }
        else if (!string.IsNullOrWhiteSpace(objectName))
        {
            var obj = pkg.GetExportOrNull(objectName, StringComparison.OrdinalIgnoreCase)
                      ?? throw new KeyNotFoundException(
                          $"包 '{normalized}' 里没有名为 '{objectName}' 的 export。" +
                          $"该包共 {pkg.ExportMapLength} 个 export，可先用 exportIndex 逐个查看。");
            payload = obj;
            selection = $"objectName={objectName}";
        }
        else
        {
            payload = pkg.GetExports().ToArray();
            selection = "all";
        }

        string json;
        try
        {
            json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"序列化失败: {e.Message}", e);
        }

        var truncated = json.Length > maxBytes;
        if (truncated) json = json[..maxBytes];

        return new
        {
            sessionId,
            packagePath = normalized,
            selection,
            exportCount = pkg.ExportMapLength,
            importCount = pkg.ImportMapLength,
            truncated,
            returnedBytes = json.Length,
            hint = truncated
                ? "内容已截断。用 objectName 或 exportIndex 精确定位单个 export 可拿到完整内容。"
                : null,
            json
        };
    }
}
