namespace FModel.Mcp.Indexing;

/// <summary>
/// 指标合理性校验。
///
/// 存在的理由（有实测依据）：不完整的 pak 子集会让 CUE4Parse **静默**产出错误数据且不抛异常。
/// 实测尘白 102-pak 子集 vs 633-pak 完整安装：
///   NumVertices  27,710(正确)  →  0 / 2,097,152 / 340,328,448(垃圾)
///   Sections     0 个 null     →  46/60 含 null
///   Indices      122,898 字节  →  0 或 null
/// 而反序列化全程不报错。若原样上报，Agent 会以为"这个模型有 3.4 亿顶点"并据此推理。
/// </summary>
public static class Sanity
{
    /// <summary>顶点数上限：单个 mesh 超过这个量级几乎必然是错位读取。</summary>
    private const int MaxPlausibleVertices = 20_000_000;

    /// <summary>顶点数相对三角数的上限倍数。正常网格 vtx ≈ tri×(0.5~1.2)，留足余量。</summary>
    private const int MaxVertexPerTriangleRatio = 6;

    private const int MaxPlausibleBones = 20_000;
    private const int MaxPlausibleTextureEdge = 65_536;

    public static void Validate(AssetRecord r)
    {
        ValidateVertices(r);
        ValidateTriangles(r);
        ValidateBones(r);
        ValidateTexture(r);
    }

    private static void ValidateVertices(AssetRecord r)
    {
        if (r.VerticesLod0 is not { } vtx) return;

        if (vtx < 0)
        {
            Drop(r, ref r.VerticesLod0, "顶点数为负");
            return;
        }
        if (vtx > MaxPlausibleVertices)
        {
            Drop(r, ref r.VerticesLod0, $"顶点数 {vtx:N0} 超出合理范围，判定为错位读取");
            return;
        }
        if (r.TrianglesLod0 is > 0 and var tri && vtx > (long)tri * MaxVertexPerTriangleRatio)
        {
            Drop(r, ref r.VerticesLod0, $"顶点数 {vtx:N0} 相对三角数 {tri:N0} 不合理");
        }
    }

    private static void ValidateTriangles(AssetRecord r)
    {
        if (r.TrianglesLod0 is < 0) Drop(r, ref r.TrianglesLod0, "LOD0 三角数为负");
        if (r.TrianglesAll is < 0) Drop(r, ref r.TrianglesAll, "全 LOD 三角数为负");

        // 全 LOD 总和不该小于 LOD0
        if (r.TrianglesAll is { } all && r.TrianglesLod0 is { } lod0 && all < lod0)
            r.MarkSuspect($"全 LOD 三角数 {all:N0} 小于 LOD0 {lod0:N0}");
    }

    private static void ValidateBones(AssetRecord r)
    {
        if (r.Bones is { } b && (b < 0 || b > MaxPlausibleBones))
            Drop(r, ref r.Bones, $"骨骼数 {b:N0} 超出合理范围");

        if (r.ActiveBonesLod0 is { } ab && r.Bones is { } total && total > 0 && ab > total)
            r.MarkSuspect($"LOD0 活动骨骼 {ab} 多于总骨骼 {total}");
    }

    private static void ValidateTexture(AssetRecord r)
    {
        if (r.TexMaxEdge is { } e && (e <= 0 || e > MaxPlausibleTextureEdge))
        {
            Drop(r, ref r.TexMaxEdge, $"贴图最大边 {e} 超出合理范围");
            r.TexExactRes = null;
        }
    }

    private static void Drop<T>(AssetRecord r, ref T? field, string reason) where T : struct
    {
        field = null;
        r.MarkSuspect(reason);
    }

    /// <summary>
    /// 数据集完整性判定。索引完成后跑一次，结果写进 meta 并在 describe_dataset / open_game 返回。
    /// </summary>
    public static DatasetIntegrity Judge(int meshTotal, int meshSuspect)
    {
        if (meshTotal == 0) return new DatasetIntegrity("unknown", 0, 0, 0d,
            "样本中没有 Mesh，无法判定数据集完整性。");

        var rate = meshSuspect / (double)meshTotal;
        return rate switch
        {
            >= 0.30 => new DatasetIntegrity("suspect", meshTotal, meshSuspect, rate,
                $"{meshSuspect}/{meshTotal} ({rate:P0}) 的 Mesh 指标异常 —— 该 pak 集合很可能不完整（例如只从完整安装里挑了部分 pak）。" +
                "这些资产的面数/顶点数不可信，且导出会失败。建议改用游戏的完整安装目录。"),
            >= 0.05 => new DatasetIntegrity("partial", meshTotal, meshSuspect, rate,
                $"{meshSuspect}/{meshTotal} ({rate:P0}) 的 Mesh 指标异常，属少数个例。统计时建议加 WHERE integrity='ok'。"),
            _ => new DatasetIntegrity("ok", meshTotal, meshSuspect, rate,
                meshSuspect == 0 ? "全部 Mesh 指标正常。" : $"仅 {meshSuspect} 个 Mesh 异常，可忽略。")
        };
    }
}

public sealed record DatasetIntegrity(
    string Status,          // ok | partial | suspect | unknown
    int MeshTotal,
    int MeshSuspect,
    double SuspectRate,
    string Message);
