using System.ComponentModel;
using System.Diagnostics;
using FModel.Mcp.Session;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FModel.Mcp.Tools;

[McpServerToolType]
public static class QueryIndexTool
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 5000;
    private const int TimeoutSeconds = 15;

    [McpServerTool(Name = "query_index")]
    [Description("""
        对资产索引执行只读 SQL（SQLite 方言）。这是做统计分析的主力工具。

        表结构见 describe_dataset 的 indexSchema 字段。只允许单条 SELECT / WITH 语句。

        示例 —— 某个角色的资产类型分布：
          SELECT class_name, COUNT(*) n, SUM(triangles_lod0) tri
          FROM assets WHERE package_path LIKE 'Game/Content/Characters/Girl/girl009b%'
          GROUP BY class_name ORDER BY n DESC

        示例 —— 贴图分辨率直方图：
          SELECT tex_exact_res, COUNT(*) n FROM assets
          WHERE class_name LIKE 'Texture%' AND integrity='ok'
          GROUP BY tex_exact_res ORDER BY n DESC

        示例 —— LOD0 面数 P90：
          SELECT triangles_lod0 FROM assets WHERE class_name='StaticMesh' AND integrity='ok'
          ORDER BY triangles_lod0
          LIMIT 1 OFFSET (SELECT CAST(COUNT(*)*0.9 AS INT) FROM assets
                          WHERE class_name='StaticMesh' AND integrity='ok')

        注意：统计数值指标时加 WHERE integrity='ok'，否则会把读取失败的垃圾值算进去。
        """)]
    public static CallToolResult QueryIndex(
        SessionManager sessions,
        [Description("open_game 返回的 sessionId")] string sessionId,
        [Description("单条只读 SQL（SELECT 或 WITH 开头）")] string sql,
        [Description("最多返回行数，默认 200，上限 5000")] int limit = DefaultLimit)
        => ToolResponse.Run("query_index", () => Execute(sessions, sessionId, sql, limit));

    private static object Execute(SessionManager sessions, string sessionId, string sql, int limit)
    {
        var s = sessions.Require(sessionId);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var guard = Validate(sql);
        if (guard is not null) throw new ArgumentException(guard);

        if (!File.Exists(s.IndexPath))
            throw new InvalidOperationException(
                "索引尚未创建。用 index_status 查看进度；索引就绪前可先用 list_dirs / find_references 做结构发现。");

        var sw = Stopwatch.StartNew();
        using var cn = new SqliteConnection($"Data Source={s.IndexPath};Mode=ReadOnly");
        cn.Open();

        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = TimeoutSeconds;

        var columns = new List<string>();
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        try
        {
            using var reader = cmd.ExecuteReader();
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));

            while (reader.Read())
            {
                if (rows.Count >= limit) { truncated = true; break; }
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }
        catch (SqliteException e)
        {
            throw new ArgumentException(
                $"SQL 执行失败: {e.Message}\n" +
                $"表结构请参考 describe_dataset 的 indexSchema 字段。", e);
        }

        var p = s.Progress;
        return new
        {
            sessionId,
            columns,
            rowCount = rows.Count,
            truncated,
            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            indexComplete = s.IndexReady,
            indexProgress = p.Percentage,
            warning = s.IndexReady
                ? null
                : $"索引尚未完成（{p.Percentage:P0}），结果只覆盖已索引的部分，统计值会偏低。",
            integrityWarning = s.Integrity is { Status: "suspect" } bad ? bad.Message : null,
            rows
        };
    }

    /// <summary>只读闸门：单条语句 + 必须 SELECT/WITH 开头 + 禁写关键字。</summary>
    private static string? Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "SQL 不能为空。";

        var trimmed = StripComments(sql).Trim().TrimEnd(';').Trim();
        if (trimmed.Length == 0) return "SQL 不能为空。";

        if (trimmed.Contains(';'))
            return "只允许单条语句，请去掉语句中间的分号。";

        var head = trimmed.Split([' ', '\t', '\r', '\n', '('], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToUpperInvariant() ?? "";
        if (head is not ("SELECT" or "WITH"))
            return $"只允许只读查询，SQL 必须以 SELECT 或 WITH 开头（当前以 '{head}' 开头）。";

        string[] banned = ["INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE",
                           "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX", "REPLACE"];
        var upper = " " + trimmed.ToUpperInvariant() + " ";
        foreach (var kw in banned)
            if (upper.Contains($" {kw} ", StringComparison.Ordinal))
                return $"检测到写操作关键字 '{kw}'。索引是只读的，只能查询。";

        return null;
    }

    private static string StripComments(string sql)
    {
        var noBlock = System.Text.RegularExpressions.Regex.Replace(
            sql, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(
            noBlock, @"--[^\n]*", " ");
    }
}
