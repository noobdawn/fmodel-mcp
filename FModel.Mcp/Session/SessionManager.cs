using System.Collections.Concurrent;

namespace FModel.Mcp.Session;

/// <summary>
/// 会话缓存。Provider 常驻内存（完整尘白 71 万文件条目约 1.5–3 GB），
/// 因此上限 2 个并按 LRU 淘汰；索引在磁盘上，被淘汰的会话重开只需几秒。
/// </summary>
public sealed class SessionManager : IDisposable
{
    public const int MaxSessions = 2;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _admissionGate = new();
    private readonly Timer _reaper;

    public SessionManager()
    {
        _reaper = new Timer(_ => ReapIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static string RootDirectory =>
        Environment.GetEnvironmentVariable("FMODEL_MCP_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FModelMcp");

    public static string IndexRoot =>
        Environment.GetEnvironmentVariable("FMODEL_MCP_INDEX_DIR")
        ?? Path.Combine(RootDirectory, "index");

    public IReadOnlyCollection<GameSession> All => _sessions.Values.ToList();

    /// <summary>登记新会话；超出上限时淘汰最久未访问的。</summary>
    public void Admit(GameSession session)
    {
        lock (_admissionGate)
        {
            while (_sessions.Count >= MaxSessions)
            {
                var victim = _sessions.Values.MinBy(s => s.LastAccess);
                if (victim is null) break;
                Close(victim.Id);
            }
            _sessions[session.Id] = session;
        }
    }

    public bool TryGet(string id, out GameSession session)
    {
        if (_sessions.TryGetValue(id, out var s))
        {
            s.Touch();
            session = s;
            return true;
        }
        session = null!;
        return false;
    }

    /// <summary>取会话，取不到就抛出对 Agent 友好的说明。</summary>
    public GameSession Require(string sessionId)
    {
        if (TryGet(sessionId, out var s)) return s;
        var alive = _sessions.Keys.ToList();
        throw new InvalidOperationException(
            $"会话 '{sessionId}' 不存在或已过期（{IdleTimeout.TotalMinutes:F0} 分钟无访问会回收）。" +
            (alive.Count > 0 ? $" 当前存活会话: {string.Join(", ", alive)}。" : " 当前无存活会话。") +
            " 请重新调用 open_game —— 索引在磁盘上，重开通常只需几秒。");
    }

    public bool Close(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return false;
        s.Dispose();
        return true;
    }

    private void ReapIdle()
    {
        var deadline = DateTime.UtcNow - IdleTimeout;
        foreach (var s in _sessions.Values.Where(s => s.LastAccess < deadline).ToList())
            Close(s.Id);
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var id in _sessions.Keys.ToList()) Close(id);
    }
}
