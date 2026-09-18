namespace FModel.Mcp.Runtime;

/// <summary>
/// 导出路径解析与安全校验。
///
/// MCP 的写权限只覆盖两处：索引目录和导出目录。
/// 这里强制所有导出产物落在导出根之下 —— 拒绝 <c>..\</c> 穿越、绝对路径逃逸、
/// 以及任何指向游戏目录或 FModel 配置的写入。
/// </summary>
public static class ExportPaths
{
    /// <summary>导出根。可用 FMODEL_MCP_EXPORT_DIR 覆盖（C 盘空间紧张时很有用）。</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("FMODEL_MCP_EXPORT_DIR")
        ?? Path.Combine(Session.SessionManager.RootDirectory, "exports");

    /// <summary>
    /// 解析某次导出的目标目录：&lt;Root&gt;/&lt;游戏名&gt;/&lt;subdir&gt;。
    /// subdir 为空时省略。任何越界都抛异常而不是静默改写。
    /// </summary>
    public static string Resolve(string gameName, string? subdir)
    {
        var safeGame = Sanitize(gameName, fallback: "game");
        var root = Path.GetFullPath(Root);
        var baseDir = Path.GetFullPath(Path.Combine(root, safeGame));

        if (string.IsNullOrWhiteSpace(subdir))
        {
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        var normalized = subdir.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        if (Path.IsPathRooted(normalized))
            throw new ArgumentException(
                $"outputSubdir 必须是相对路径，收到绝对路径 '{subdir}'。" +
                $"导出只允许写入 {root} 之下。");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new ArgumentException(
                $"outputSubdir 不允许包含 '..'（收到 '{subdir}'）。导出只允许写入 {root} 之下。");

        var target = Path.GetFullPath(Path.Combine(baseDir, Path.Combine([.. segments.Select(s => Sanitize(s, "_"))])));

        // 双保险：规范化后仍必须在 baseDir 之下
        if (!target.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !target.Equals(baseDir, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"解析后的输出路径 '{target}' 越出了允许范围 '{baseDir}'。");

        Directory.CreateDirectory(target);
        return target;
    }

    /// <summary>去掉文件系统非法字符，保留可读性。</summary>
    private static string Sanitize(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. raw.Select(c => invalid.Contains(c) ? '_' : c)]).Trim(' ', '.');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>统计目录下产物，用于导出后汇报。</summary>
    public static (int Files, long Bytes) Measure(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
            return (files.Count, files.Sum(f => new FileInfo(f).Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }
}
