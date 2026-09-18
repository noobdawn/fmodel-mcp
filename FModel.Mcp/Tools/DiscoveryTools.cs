using System.ComponentModel;
using FModel.Mcp.Indexing;
using FModel.Mcp.Session;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp.Tools;

[McpServerToolType]
public static class DescribeDatasetTool
{
    [McpServerTool(Name = "describe_dataset")]
    [Description("""
        返回这个游戏的资产组织概览 —— 顶层目录、类型分布、各类型的命名样例、索引 SQL schema、数据集完整性。

        第一次接触一个游戏时应该先调这个，它能省掉十几轮盲目试探：
        你会直接看到资产按什么目录组织、有哪些类、命名长什么样，然后就能写出正确的 query_index SQL。
        """)]
    public static CallToolResult DescribeDataset(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("命名样例每类返回几个，默认 5")] int samplesPerClass = 5)
        => ToolResponse.Run("describe_dataset", () => Execute(sessions, sessionId, samplesPerClass));

    private static object Execute(SessionManager sessions, string sessionId, int samplesPerClass)
    {
        var s = sessions.Require(sessionId);
        samplesPerClass = Math.Clamp(samplesPerClass, 0, 20);

        // 顶层目录来自内存态 Provider.Files，不依赖索引 —— 索引没好也能用
        var topDirs = s.Provider.Files.Keys
            .Select(TopSegments)
            .Where(p => p.Length > 0)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { path = g.Key, files = g.Count() })
            .OrderByDescending(x => x.files)
            .Take(25)
            .ToList();

        var classDistribution = new List<object>();
        var namingSamples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var indexedRows = 0;
        var refEdges = 0L;
        var ioStoreSkipped = 0;

        if (File.Exists(s.IndexPath))
        {
            try
            {
                using var cn = new SqliteConnection($"Data Source={s.IndexPath};Mode=ReadOnly");
                cn.Open();

                indexedRows = ScalarInt(cn, "SELECT COUNT(*) FROM assets");
                refEdges = ScalarLong(cn, "SELECT COUNT(*) FROM refs");

                // 复用既有索引时 session.Indexer 为 null，只能从 meta 取
                ioStoreSkipped = s.Indexer?.IoStoreSkipped
                    ?? (int.TryParse(IndexSchema.GetMeta(cn, "ref_graph_iostore_skipped"), out var v) ? v : 0);

                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT class_name, COUNT(*) n,
                               SUM(CASE WHEN integrity<>'ok' THEN 1 ELSE 0 END) suspect
                        FROM assets GROUP BY class_name ORDER BY n DESC LIMIT 40
                        """;
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        classDistribution.Add(new
                        {
                            className = r.GetString(0),
                            count = r.GetInt32(1),
                            suspect = r.IsDBNull(2) ? 0 : r.GetInt32(2)
                        });
                }

                if (samplesPerClass > 0)
                {
                    foreach (var cls in new[] { "SkeletalMesh", "StaticMesh", "Texture2D", "AnimSequence", "MaterialInstanceConstant" })
                    {
                        using var cmd = cn.CreateCommand();
                        cmd.CommandText = "SELECT package_path FROM assets WHERE class_name=$c LIMIT $n";
                        cmd.Parameters.AddWithValue("$c", cls);
                        cmd.Parameters.AddWithValue("$n", samplesPerClass);
                        using var r = cmd.ExecuteReader();
                        var list = new List<string>();
                        while (r.Read()) list.Add(r.GetString(0));
                        if (list.Count > 0) namingSamples[cls] = list;
                    }
                }
            }
            catch (SqliteException e)
            {
                classDistribution.Add(new { error = $"读取索引失败: {e.Message}" });
            }
        }

        var p = s.Progress;
        return new
        {
            sessionId,
            gameDirectory = s.GameDirectory,
            projectName = s.Provider.ProjectName,
            scope = s.Scope,
            totalFilesInProvider = s.Provider.Files.Count,
            index = new
            {
                status = p.Phase,
                percentage = p.Percentage,
                indexedRows,
                refEdges,
                ioStoreSkipped,
                refGraphWarning = ioStoreSkipped > 0 && refEdges == 0
                    ? $"引用图为空：{ioStoreSkipped} 个包是 IoStore 格式（.utoc/.ucas），" +
                      "当前只支持传统 .pak 的 import 表。find_references(incoming) 与 expand_references 对这个游戏不可用，" +
                      "只能靠 package_path 前缀 / 命名规律做关联分析。"
                    : null,
                ready = s.IndexReady,
                path = s.IndexPath
            },
            datasetIntegrity = s.Integrity,
            topDirectories = topDirs,
            classDistribution,
            namingSamples,
            indexSchema = IndexSchema.HumanReadableSchema,
            hints = new[]
            {
                "用 list_dirs 逐层下钻可以看清目录结构，成本极低（纯内存前缀聚合）。",
                "统计数值指标时建议加 WHERE integrity='ok'，suspect 行的面数/顶点数不可信。",
                "一个 package 可能有多个 export（多行），统计模型数用 COUNT(DISTINCT package_path)。",
                "找某个实体（如角色）的全部资源：先 list_dirs 定位目录，再 query_index 按 package_path LIKE 筛，最后用 find_references 补上不在该目录下的材质与贴图。"
            }
        };
    }

    private static string TopSegments(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0],
            _ => $"{parts[0]}/{parts[1]}"
        };
    }

    private static int ScalarInt(SqliteConnection cn, string sql)
    {
        using var c = cn.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is long v ? (int)v : 0;
    }

    private static long ScalarLong(SqliteConnection cn, string sql)
    {
        using var c = cn.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteScalar() is long v ? v : 0L;
    }
}

[McpServerToolType]
public static class ListDirsTool
{
    [McpServerTool(Name = "list_dirs")]
    [Description("""
        列出某个路径前缀下的目录树（不是文件列表）。纯内存前缀聚合，毫秒级，不依赖索引。

        这是结构发现的主力工具：想知道"这个游戏的角色怎么组织的"，
        就从 list_dirs("Game/Content/Characters/") 开始逐层下钻。

        目录多时（例如 231 个角色目录）务必用 pattern 过滤，或把 sortBy 设为 "path" 按名称排序 ——
        默认按文件数排序 + limit 截断会漏掉你要找的目录。
        """)]
    public static CallToolResult ListDirs(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("路径前缀，例如 Game/Content/Characters/ 。留空则列出根级目录")] string prefix = "",
        [Description("向下展开几层，默认 1，最大 4")] int depth = 1,
        [Description("子串过滤（不区分大小写），只保留路径含该串的目录。例如 \"girl008b\"")]
        string? pattern = null,
        [Description("排序方式：files(默认,按文件数降序) | path(按路径名升序) | size(按体积降序)")]
        string sortBy = "files",
        [Description("最多返回多少个目录节点，默认 200")] int limit = 200)
        => ToolResponse.Run("list_dirs", () => Execute(sessions, sessionId, prefix, depth, pattern, sortBy, limit));

    private static object Execute(
        SessionManager sessions, string sessionId, string prefix, int depth,
        string? pattern, string sortBy, int limit)
    {
        var s = sessions.Require(sessionId);
        depth = Math.Clamp(depth, 1, 4);
        limit = Math.Clamp(limit, 1, 2000);

        var norm = prefix.Replace('\\', '/').TrimStart('/');
        if (norm.Length > 0 && !norm.EndsWith('/')) norm += "/";

        var counts = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var directFiles = 0;

        foreach (var (path, file) in s.Provider.Files)
        {
            if (norm.Length > 0 && !path.StartsWith(norm, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[norm.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) { directFiles++; continue; }

            var segments = rest.Split('/');
            for (var d = 1; d <= Math.Min(depth, segments.Length - 1); d++)
            {
                var key = norm + string.Join('/', segments[..d]);
                var prev = counts.GetValueOrDefault(key);
                counts[key] = (prev.Files + 1, prev.Bytes + file.Size);
            }
        }

        var matched = counts.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(pattern))
            matched = matched.Where(kv => kv.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase));

        var matchedList = matched.ToList();

        var ordered = sortBy.ToLowerInvariant() switch
        {
            "path" => matchedList.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
            "size" => matchedList.OrderByDescending(kv => kv.Value.Bytes),
            _ => matchedList.OrderByDescending(kv => kv.Value.Files)
        };

        var nodes = ordered
            .Take(limit)
            .Select(kv => new
            {
                path = kv.Key + "/",
                depth = kv.Key[norm.Length..].Count(c => c == '/') + 1,
                files = kv.Value.Files,
                sizeMB = Math.Round(kv.Value.Bytes / 1024d / 1024d, 1)
            })
            .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var truncated = matchedList.Count > limit;

        return new
        {
            sessionId,
            prefix = norm,
            depth,
            pattern,
            sortBy,
            directFileCount = directFiles,
            directoryCount = counts.Count,
            matchedCount = matchedList.Count,
            returnedCount = nodes.Count,
            truncated,
            truncationHint = truncated
                ? $"匹配 {matchedList.Count} 个目录但只返回了 {nodes.Count} 个。" +
                  "用 pattern 缩小范围，或调大 limit，或把 sortBy 设为 \"path\" 按名称排序后分段查看。"
                : null,
            directories = nodes
        };
    }
}
