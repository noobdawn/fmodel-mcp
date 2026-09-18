using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace FModel.Mcp.Indexing;

/// <summary>
/// 从 UObject 提取索引指标。
/// 只取「解析阶段已在内存里」的标量 —— 实测 Texture 元数据 0.0 ms、Mesh 0.5~6 ms，
/// 大数据（像素/音频/Nanite cluster）走 lazy bulk data，此处一概不触碰。
/// </summary>
public static class MetricExtractor
{
    private static readonly HashSet<string> DeepClasses = new(StringComparer.Ordinal)
    {
        "StaticMesh", "SkeletalMesh",
        "Texture2D", "Texture2DArray", "TextureCube", "VolumeTexture",
        "VirtualTexture2D", "LightMapTexture2D", "ShadowMapTexture2D",
        "AnimSequence", "AnimMontage", "AnimStreamable",
        "SoundWave",
    };

    /// <summary>只对能产出指标的类型做完整反序列化，其余仅记类名。</summary>
    public static bool NeedsDeserialization(string className) => DeepClasses.Contains(className);

    public static void Fill(AssetRecord rec, UObject obj)
    {
        switch (obj)
        {
            case UStaticMesh sm: FillStaticMesh(rec, sm); break;
            case USkeletalMesh sk: FillSkeletalMesh(rec, sk); break;
            case UTexture tex: FillTexture(rec, tex); break;
            case UAnimSequence anim: FillAnim(rec, anim); break;
            case UAnimSequenceBase seqBase: FillAnimBase(rec, seqBase); break;
            default: FillGenericAudio(rec, obj); break;
        }
    }

    // ────────────────────────────── Mesh ──────────────────────────────

    private static void FillStaticMesh(AssetRecord rec, UStaticMesh mesh)
    {
        rec.MaterialSlots = mesh.StaticMaterials.Length > 0 ? mesh.StaticMaterials.Length : mesh.Materials.Length;

        var nanite = mesh.RenderData?.NaniteResources;
        if (nanite is not null && nanite.NumClusters > 0)
        {
            rec.IsNanite = true;
            rec.TrianglesNanite = nanite.NumInputTriangles;
            rec.VerticesNanite = nanite.NumInputVertices;
        }
        else rec.IsNanite = false;

        var lods = mesh.RenderData?.LODs;
        if (lods is null || lods.Length == 0)
        {
            rec.MarkSuspect("StaticMesh 无 RenderData.LODs");
            return;
        }

        rec.LodCount = lods.Length;
        rec.TrianglesLod0 = SumTriangles(lods[0]);
        rec.TrianglesAll = lods.Sum(SumTriangles);
        rec.VerticesLod0 = lods[0].PositionVertexBuffer?.Verts?.Length ?? 0;

        if (lods[0].SkipLod) rec.MarkSuspect("LOD0 缺少顶点/索引缓冲（数据可能不完整）");
        return;

        static int SumTriangles(FStaticMeshLODResources lod) =>
            lod.Sections?.Where(s => s is not null).Sum(s => s.NumTriangles) ?? 0;
    }

    private static void FillSkeletalMesh(AssetRecord rec, USkeletalMesh mesh)
    {
        rec.Bones = mesh.ReferenceSkeleton?.FinalRefBoneInfo?.Length ?? 0;
        rec.MorphTargets = mesh.MorphTargets?.Length ?? 0;
        rec.MaterialSlots = mesh.SkeletalMaterials?.Length ?? mesh.Materials.Length;

        var nanite = mesh.NaniteResources;
        if (nanite is not null && nanite.NumClusters > 0)
        {
            rec.IsNanite = true;
            rec.TrianglesNanite = nanite.NumInputTriangles;
            rec.VerticesNanite = nanite.NumInputVertices;
        }
        else rec.IsNanite = false;

        var lods = mesh.LODModels;
        if (lods is null || lods.Length == 0)
        {
            rec.MarkSuspect("SkeletalMesh 无 LODModels");
            return;
        }

        rec.LodCount = lods.Length;
        var lod0 = lods[0];

        // ★ Sections 数组可能含 null 元素（数据不完整时），必须逐元素防御
        var nullSections = lod0.Sections?.Count(s => s is null) ?? 0;
        if (nullSections > 0) rec.MarkSuspect($"LOD0 有 {nullSections} 个空 Section（数据可能不完整）");

        rec.TrianglesLod0 = SumTriangles(lod0);
        rec.TrianglesAll = lods.Sum(SumTriangles);
        rec.ActiveBonesLod0 = lod0.ActiveBoneIndices?.Length ?? 0;

        // LOD.NumVertices 在部分游戏下不被填充，回退到 Section 求和
        var vtx = lod0.NumVertices > 0
            ? lod0.NumVertices
            : lod0.Sections?.Where(s => s is not null).Sum(s => s.NumVertices) ?? 0;
        rec.VerticesLod0 = vtx;

        if ((lod0.Indices?.Buffer?.Length ?? 0) == 0)
            rec.MarkSuspect("LOD0 索引缓冲为空（数据集很可能不完整，该模型无法导出）");
        return;

        static int SumTriangles(FStaticLODModel lod) =>
            lod.Sections?.Where(s => s is not null).Sum(s => s.NumTriangles) ?? 0;
    }

    // ────────────────────────────── Texture ──────────────────────────────

    private static void FillTexture(AssetRecord rec, UTexture tex)
    {
        var pd = tex.PlatformData;
        rec.TexWidth = pd?.SizeX;
        rec.TexHeight = pd?.SizeY;
        rec.TexFormat = pd?.PixelFormat;
        rec.TexMips = pd?.Mips?.Length;
        rec.TexKind = tex switch
        {
            UTexture2DArray => "2d_array",
            UTextureCube => "cube",
            UVolumeTexture => "volume",
            UVirtualTexture2D => "virtual",
            _ => "2d"
        };

        if (pd is { SizeX: > 0, SizeY: > 0 })
        {
            rec.TexMaxEdge = Math.Max(pd.SizeX, pd.SizeY);
            rec.TexExactRes = $"{pd.SizeX}x{pd.SizeY}";
        }
        else rec.MarkSuspect("贴图尺寸为 0");
    }

    // ────────────────────────────── Animation ──────────────────────────────

    private static void FillAnim(AssetRecord rec, UAnimSequence anim)
    {
        rec.AnimFrames = anim.NumFrames > 0 ? anim.NumFrames : null;
        FillAnimBase(rec, anim);
        if (anim.CompressedDataStructure is null)
            rec.MarkSuspect("CompressedDataStructure 为空（动画数据未读出，无法导出）");
    }

    private static void FillAnimBase(AssetRecord rec, UAnimSequenceBase seq)
    {
        rec.AnimDuration = seq.SequenceLength > 0f ? seq.SequenceLength : null;
        rec.AnimRateScale = seq.RateScale != 0f ? seq.RateScale : null;
    }

    // ────────────────────────────── Audio ──────────────────────────────

    private static void FillGenericAudio(AssetRecord rec, UObject obj)
    {
        var duration = obj.GetOrDefault("Duration", -1f);
        if (duration > 0f) rec.AudioDuration = duration;

        var channels = obj.GetOrDefault("NumChannels", -1);
        if (channels > 0) rec.AudioChannels = channels;

        var rate = obj.GetOrDefault("SampleRate", -1);
        if (rate > 0) rec.AudioSampleRate = rate;
    }
}
