using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using FModel.Mcp.Indexing;

namespace FModel.Mcp.Session;

/// <summary>参数来源，让 Agent 能判断"这个 UE 版本到底哪来的"。</summary>
public sealed record Resolved<T>(T? Value, string Source)
{
    public static Resolved<T> Explicit(T v) => new(v, "explicit");
    public static Resolved<T> FromFModel(T v) => new(v, "fmodel_config");
    public static Resolved<T> Default(T v) => new(v, "default");
    public static Resolved<T> None() => new(default, "none");
}

public sealed class GameSession : IDisposable
{
    public required string Id { get; init; }
    public required string GameDirectory { get; init; }
    public required AbstractVfsFileProvider Provider { get; init; }
    public required string IndexPath { get; init; }
    public required string IndexDirectory { get; init; }
    public required IReadOnlyDictionary<string, object?> ResolvedParameters { get; init; }
    public required IReadOnlyList<string> Scope { get; init; }

    /// <summary>索引参数快照 —— 复用既有索引时 Indexer 为 null，增量索引需要用它临时构造。</summary>
    public required IndexOptions IndexOptions { get; init; }

    /// <summary>
    /// 本次打开时映射文件（usmap/jmap）的指纹。复用既有索引前必须与索引内记录比对 ——
    /// UE5 无版本化属性游戏离开了映射什么都读不出来，映射更新后旧索引必须失效重建。
    /// </summary>
    public required string MappingsFingerprint { get; init; }

    /// <summary>按需构造 Indexer 做增量索引（主扫描的 Indexer 可能不存在）。</summary>
    public Indexer CreateIncrementalIndexer() => new(Provider, IndexPath, IndexOptions);

    public Indexer? Indexer { get; set; }
    public Task? IndexTask { get; set; }
    public CancellationTokenSource? IndexCts { get; set; }
    public DatasetIntegrity? Integrity { get; set; }

    /// <summary>复用磁盘上既有索引时为 true —— 此时没有 Indexer，进度直接算作已完成。</summary>
    public bool IndexReused { get; set; }

    /// <summary>复用索引时的行数，用于让 index_status / describe_dataset 汇报真实规模。</summary>
    public int ReusedRowCount { get; set; }

    private long _lastAccessTicks = DateTime.UtcNow.Ticks;
    public DateTime LastAccess => new(Interlocked.Read(ref _lastAccessTicks), DateTimeKind.Utc);
    public void Touch() => Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);

    public IndexProgress Progress => Indexer?.Progress
        ?? (IndexReused
            ? new IndexProgress("done", ReusedRowCount, ReusedRowCount, 0, 0, 0)
            : new IndexProgress("pending", 0, 0, 0, 0, 0));

    public bool IndexReady => Progress.Phase is "done";

    public void Dispose()
    {
        try { IndexCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { IndexTask?.Wait(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is AggregateException or OperationCanceledException) { }
        IndexCts?.Dispose();
        Provider.Dispose();
    }

    /// <summary>索引目录键 = 规范化路径 + 内容指纹（不读文件内容，只看目录元数据）。</summary>
    public static string ComputeIndexKey(string gameDirectory, IReadOnlyList<string> scope)
    {
        var sb = new StringBuilder();
        try { sb.Append(Path.GetFullPath(gameDirectory).TrimEnd('\\', '/').ToLowerInvariant()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { sb.Append(gameDirectory); }

        sb.Append("\u0000scope=").Append(string.Join(',', scope.OrderBy(s => s, StringComparer.Ordinal)));
        sb.Append("\u0000schema=").Append(IndexSchema.Version);
        sb.Append('\u0000').Append(Fingerprint(gameDirectory));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>容器文件的 (名称,长度,修改时间) 指纹 —— 游戏更新后自动失效。</summary>
    private static string Fingerprint(string gameDirectory)
    {
        try
        {
            var files = new DirectoryInfo(gameDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.Extension is ".pak" or ".utoc" or ".ucas")
                .OrderBy(f => f.FullName, StringComparer.Ordinal)
                .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
            return string.Join('\n', files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unavailable";
        }
    }

    /// <summary>
    /// 映射文件指纹（全路径|大小|修改时间）。无映射时返回 "none"。
    /// 存进索引 meta 与后续打开比对：不一致说明映射已更新，索引须重建。
    /// </summary>
    public static string ComputeMappingsFingerprint(string? mappingsPath)
    {
        if (string.IsNullOrWhiteSpace(mappingsPath)) return "none";
        try
        {
            var fi = new FileInfo(mappingsPath);
            if (!fi.Exists) return "missing";
            return $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "invalid";
        }
    }
}
