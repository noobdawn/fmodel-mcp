using System.ComponentModel;
using System.Diagnostics;
using CUE4Parse.UE4.Assets;
using FModel.Mcp.Indexing;
using FModel.Mcp.Session;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp.Tools;

[McpServerToolType]
public static class ExpandReferencesTool
{
    [McpServerTool(Name = "expand_references")]
    [Description("""
        从多个起点批量沿引用图展开，返回按类型聚合的结果，并可把发现的包自动补进索引。

        解决两个 find_references 做不到的事：
        1) 一次只能查一个包 —— 统计"这个角色引用的全部共享贴图"要循环调几十次；
        2) scope 之外的共享资产（材质库、Ramp、Matcap、IDMap 等）不在索引里，SQL 查不到。

        典型用法 —— 把某角色全部 SkeletalMesh 作为起点展开两层，并把发现的外部包补进索引：
          先 query_index 拿到 package_path 列表，再传给 roots，autoIndex=true。
        之后就能用 query_index 对这些共享资产做统计。
        """)]
    public static CallToolResult ExpandReferences(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("起点包路径数组（不含扩展名）。可先用 query_index 查出来")] string[] roots,
        [Description("沿引用链展开的层数，默认 2，最大 4")] int maxDepth = 2,
        [Description("是否把展开发现的包自动补进索引，默认 true —— 这是让 scope 外共享资产可被 SQL 统计的关键")]
        bool autoIndex = true,
        [Description("只统计/补索引这些类名（补索引后按 class_name 过滤）。留空则全部")]
        string[]? classes = null,
        [Description("最多展开多少个包，默认 2000，上限 20000")] int limit = 2000,
        [Description("是否返回完整包路径清单，默认 false（只返回聚合，避免撑爆上下文）")]
        bool includePaths = false)
        => ToolResponse.Run("expand_references", () =>
            Execute(sessions, sessionId, roots, maxDepth, autoIndex, classes, limit, includePaths));

    private static object Execute(
        SessionManager sessions, string sessionId, string[] roots, int maxDepth,
        bool autoIndex, string[]? classes, int limit, bool includePaths)
    {
        var s = sessions.Require(sessionId);
        maxDepth = Math.Clamp(maxDepth, 1, 4);
        limit = Math.Clamp(limit, 1, 20_000);

        if (roots is null || roots.Length == 0)
            throw new ArgumentException("roots 不能为空。可先用 query_index 查出起点，例如 " +
                                        "SELECT DISTINCT package_path FROM assets WHERE class_name='SkeletalMesh'");

        var sw = Stopwatch.StartNew();
        var paths = new PackagePathNormalizer(s.Provider);

        var normalizedRoots = roots
            .Select(r => paths.Normalize(r))
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── BFS ──
        var visited = new HashSet<string>(normalizedRoots, StringComparer.OrdinalIgnoreCase);
        var discovered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // path -> 最浅深度
        var frontier = new List<string>(normalizedRoots);
        var unresolved = new List<string>();
        var truncated = false;
        var ioStoreSkipped = 0;

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var pkgPath in frontier)
            {
                if (!FindReferencesTool.TryLoad(s, pkgPath, out var pkg))
                {
                    if (!normalizedRoots.Contains(pkgPath, StringComparer.OrdinalIgnoreCase))
                        unresolved.Add(pkgPath);
                    continue;
                }
                // IoStore 的 IoPackage 用 FPackageObjectIndex 而非 FObjectImport，当前不支持
                if (pkg is not Package legacy) { ioStoreSkipped++; continue; }

                foreach (var import in legacy.ImportMap)
                {
                    if (!import.ClassName.Text.Equals("Package", StringComparison.Ordinal)) continue;
                    var raw = import.ObjectName.Text;
                    if (string.IsNullOrEmpty(raw)) continue;

                    var dst = paths.Normalize(raw);
                    if (string.IsNullOrEmpty(dst) || !visited.Add(dst)) continue;

                    if (discovered.Count >= limit) { truncated = true; break; }
                    discovered[dst] = depth;
                    next.Add(dst);
                }
                if (truncated) break;
            }
            if (truncated) break;
            frontier = next;
        }

        // ── 自动补索引 ──
        IncrementalIndexResult? indexed = null;
        if (autoIndex && discovered.Count > 0)
        {
            indexed = s.CreateIncrementalIndexer()
                .IndexPackagesAsync([.. discovered.Keys], force: false, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        // ── 聚合：优先用索引里的真实 class_name，索引里没有的按顶层目录归类 ──
        var classCounts = QueryClassCounts(s, [.. discovered.Keys], classes);
        var byTopDir = discovered.Keys
            .GroupBy(TopDir, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { directory = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(25)
            .ToList();

        var outsideScope = s.Scope.Count == 0
            ? 0
            : discovered.Keys.Count(p => !s.Scope.Any(sc => p.StartsWith(sc, StringComparison.OrdinalIgnoreCase)));

        return new
        {
            sessionId,
            rootCount = normalizedRoots.Count,
            maxDepth,
            discoveredCount = discovered.Count,
            outsideScopeCount = outsideScope,
            truncated,
            ioStoreSkipped,
            warning = FindReferencesTool.IoStoreWarning(ioStoreSkipped, discovered.Count),
            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            autoIndexed = indexed is null ? null : new
            {
                newlyIndexed = indexed.NewlyIndexed,
                alreadyIndexed = indexed.AlreadyIndexed,
                rowsWritten = indexed.RowsWritten,
                unresolved = indexed.Unresolved,
                elapsedSeconds = indexed.ElapsedSeconds
            },
            byClass = classCounts,
            byTopDirectory = byTopDir,
            unresolvedSamples = unresolved.Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList(),
            paths = includePaths
                ? discovered.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new { path = kv.Key, depth = kv.Value }).ToList()
                : null,
            hint = autoIndex
                ? "发现的包已补进索引，现在可以直接用 query_index 对它们做统计（含 scope 之外的共享资产）。"
                : "autoIndex=false，这些包未进索引，query_index 查不到。需要统计请重跑并设 autoIndex=true。"
        };
    }

    private static string TopDir(string path)
    {
        var parts = path.Split('/');
        return parts.Length <= 3 ? path : string.Join('/', parts[..3]);
    }

    /// <summary>从索引里按 package_path 批量取 class 分布；分批查避免超长 IN。</summary>
    private static List<object> QueryClassCounts(GameSession s, List<string> paths, string[]? classes)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(s.IndexPath) || paths.Count == 0) return [];

        using var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.IndexPath};Mode=ReadOnly");
        cn.Open();

        const int Chunk = 400;
        for (var offset = 0; offset < paths.Count; offset += Chunk)
        {
            var slice = paths.Skip(offset).Take(Chunk).ToList();
            using var cmd = cn.CreateCommand();
            var names = slice.Select((_, i) => $"$p{i}").ToList();
            var filter = classes is { Length: > 0 }
                ? $" AND class_name IN ({string.Join(",", classes.Select((_, i) => $"$c{i}"))})"
                : "";
            cmd.CommandText =
                $"SELECT class_name, COUNT(*) FROM assets WHERE package_path IN ({string.Join(",", names)}){filter} GROUP BY class_name";
            for (var i = 0; i < slice.Count; i++) cmd.Parameters.AddWithValue(names[i], slice[i]);
            if (classes is { Length: > 0 })
                for (var i = 0; i < classes.Length; i++) cmd.Parameters.AddWithValue($"$c{i}", classes[i]);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cls = r.GetString(0);
                result[cls] = result.GetValueOrDefault(cls) + r.GetInt32(1);
            }
        }

        return [.. result.OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new { className = kv.Key, count = kv.Value })];
    }
}

[McpServerToolType]
public static class IndexPathsTool
{
    [McpServerTool(Name = "index_paths")]
    [Description("""
        把指定的包补进现有索引，突破 open_game 时 scope 的硬边界。

        scope 限定了主扫描范围，但分析时常常需要 scope 之外的包
        （共享材质库、公共贴图、Props 目录下的道具等）。用这个工具按需补进来，之后 query_index 就能查到。

        幂等：已索引的包默认跳过；force=true 时重建（游戏更新后想刷新某些包可用）。
        """)]
    public static CallToolResult IndexPaths(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("要补索引的包路径数组（不含扩展名）。虚拟路径 /Game/... 也接受")] string[] packagePaths,
        [Description("是否强制重建已索引的包，默认 false")] bool force = false)
        => ToolResponse.Run("index_paths", () =>
        {
            var s = sessions.Require(sessionId);
            if (packagePaths is null || packagePaths.Length == 0)
                throw new ArgumentException("packagePaths 不能为空。");

            var r = s.CreateIncrementalIndexer()
                .IndexPackagesAsync(packagePaths, force, CancellationToken.None)
                .GetAwaiter().GetResult();

            return new
            {
                sessionId,
                requested = r.Requested,
                normalized = r.Normalized,
                alreadyIndexed = r.AlreadyIndexed,
                newlyIndexed = r.NewlyIndexed,
                rowsWritten = r.RowsWritten,
                refEdgesWritten = r.RefEdgesWritten,
                unresolved = r.Unresolved,
                unresolvedPaths = r.UnresolvedPaths.Take(20).ToList(),
                elapsedSeconds = r.ElapsedSeconds,
                hint = r.Unresolved > 0
                    ? "部分路径在 provider 里找不到对应文件 —— 可能是 /Script/ 这类引擎内置引用，或路径拼写有误。"
                    : null
            };
        });
}
