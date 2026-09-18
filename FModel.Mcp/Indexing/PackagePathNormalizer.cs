using System.Collections.Concurrent;
using CUE4Parse.FileProvider;

namespace FModel.Mcp.Indexing;

/// <summary>
/// 把 UE 虚拟路径规范化成索引里使用的 provider 路径。
///
/// 存在的理由（实测暴露的缺陷）：包的 import 表里存的是**虚拟路径**
/// <c>/Game/Characters/Girl/girl008b_02/girl008b_02_body01_inst</c>，
/// 而 <c>Provider.Files</c> 与索引的 package_path 用的是**挂载点路径**
/// <c>Game/Content/Characters/Girl/girl008b_02/girl008b_02_body01_inst</c>。
/// 两者对不上，导致 find_references 的结果无法直接喂回 query_index。
///
/// <see cref="IFileProvider.FixPath"/> 负责映射（含 /Game/ → &lt;ProjectName&gt;/Content/、
/// 虚拟插件路径、FortniteGame 的 GameFeatures 特例），但它会补上 .uasset 扩展名，
/// 而索引统一存不带扩展名的形式，因此这里再剥一次。
/// </summary>
public sealed class PackagePathNormalizer(IFileProvider provider)
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>失败时原样返回，绝不抛异常 —— 规范化不该让索引或查询中断。</summary>
    public string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        return _cache.GetOrAdd(raw, static (key, self) => self.Compute(key), this);
    }

    private string Compute(string raw)
    {
        try
        {
            return StripPackageExtension(provider.FixPath(raw));
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
        {
            return StripPackageExtension(raw.Replace('\\', '/').TrimStart('/'));
        }
    }

    private static readonly string[] PackageExtensions = [".uasset", ".umap", ".uexp", ".ubulk"];

    private static string StripPackageExtension(string path)
    {
        foreach (var ext in PackageExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return path[..^ext.Length];
        return path;
    }
}
