namespace FModel.Mcp.Indexing;

/// <summary>索引里的一行。可空字段表示"该类型不适用"或"读取失败"。</summary>
public sealed class AssetRecord
{
    public required string PackagePath { get; init; }
    public required string Directory { get; init; }
    public required string ObjectName { get; init; }
    public required string ClassName { get; init; }
    public required int ExportIndex { get; init; }
    public required long FileSize { get; init; }

    // Mesh
    public int? LodCount;
    public int? TrianglesLod0;
    public int? TrianglesAll;
    public int? VerticesLod0;
    public int? Bones;
    public int? ActiveBonesLod0;
    public int? MorphTargets;
    public int? MaterialSlots;
    public bool? IsNanite;
    public long? TrianglesNanite;
    public long? VerticesNanite;

    // Texture
    public int? TexWidth;
    public int? TexHeight;
    public int? TexMaxEdge;
    public string? TexExactRes;
    public string? TexFormat;
    public int? TexMips;
    public string? TexKind;

    // Animation
    public int? AnimFrames;
    public float? AnimDuration;
    public float? AnimRateScale;

    // Audio
    public float? AudioDuration;
    public int? AudioChannels;
    public int? AudioSampleRate;

    public string Integrity = "ok";
    public string? IntegrityNote;

    public void MarkSuspect(string note)
    {
        Integrity = "suspect";
        IntegrityNote = IntegrityNote is null ? note : $"{IntegrityNote}; {note}";
    }
}

/// <summary>索引进度快照。</summary>
public sealed record IndexProgress(
    string Phase,          // mounting | scanning | writing | done | failed | cancelled
    int Processed,
    int Total,
    int SuspectCount,
    long RefEdges,
    double ElapsedSeconds,
    string? Error = null)
{
    public double Percentage => Total > 0 ? Math.Round(Processed / (double)Total, 4) : 0d;
    public bool IsTerminal => Phase is "done" or "failed" or "cancelled";
}

/// <summary>增量索引结果。</summary>
public sealed record IncrementalIndexResult(
    int Requested,
    int Normalized,
    int AlreadyIndexed,
    int NewlyIndexed,
    int Unresolved,
    int RowsWritten,
    long RefEdgesWritten,
    double ElapsedSeconds,
    IReadOnlyList<string> UnresolvedPaths);
