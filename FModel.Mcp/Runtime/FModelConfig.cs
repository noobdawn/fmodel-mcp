using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace FModel.Mcp.Runtime;

/// <summary>单个游戏目录在 FModel 里的配置快照（只读）。</summary>
public sealed record FModelDirectoryConfig(
    string GameDirectory,
    string? GameName,
    EGame? UeVersion,
    ETexturePlatform? TexturePlatform,
    string? AesMainKey,
    IReadOnlyList<(string Guid, string Key)> AesDynamicKeys,
    string? MappingsFilePath);

/// <summary>
/// 只读解析 <c>%AppData%\FModel\AppSettings.json</c>。
/// 绝不写回 —— MCP 不修改用户的 FModel 配置。
/// 解析容错：字段缺失/新增/类型不符一律降级为 null，不抛异常。
/// </summary>
public sealed record FModelConfig(
    string FilePath,
    string? OutputDirectory,
    IReadOnlyDictionary<string, FModelDirectoryConfig> PerDirectory)
{
    /// <summary>FModel 存放 oodle/zlib/detex 等原生库的目录。</summary>
    public string? DataDirectory =>
        OutputDirectory is null ? null : Path.Combine(OutputDirectory, ".data");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings.json");

    private static FModelConfig? _cached;
    private static bool _attempted;

    public static FModelConfig? TryLoad(string? path = null)
    {
        if (path is null && _attempted) return _cached;

        var file = path ?? DefaultPath;
        FModelConfig? result = null;
        try
        {
            if (File.Exists(file))
            {
                var root = JObject.Parse(File.ReadAllText(file));
                result = new FModelConfig(
                    file,
                    root.Value<string>("OutputDirectory"),
                    ParsePerDirectory(root["PerDirectory"] as JObject));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            result = null;
        }

        if (path is null)
        {
            _cached = result;
            _attempted = true;
        }
        return result;
    }

    /// <summary>按游戏目录查找配置。做路径规范化，容忍尾部分隔符与大小写差异。</summary>
    public FModelDirectoryConfig? Find(string gameDirectory)
    {
        var key = Normalize(gameDirectory);
        foreach (var (k, v) in PerDirectory)
            if (Normalize(k) == key) return v;
        return null;
    }

    private static string Normalize(string p)
    {
        try { p = Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* 用原值 */ }
        return p.TrimEnd('\\', '/').ToLowerInvariant();
    }

    private static Dictionary<string, FModelDirectoryConfig> ParsePerDirectory(JObject? node)
    {
        var map = new Dictionary<string, FModelDirectoryConfig>(StringComparer.OrdinalIgnoreCase);
        if (node is null) return map;

        foreach (var (dir, valueToken) in node)
        {
            if (valueToken is not JObject v) continue;

            var dynamic = new List<(string, string)>();
            if (v["AesKeys"]?["DynamicKeys"] is JArray arr)
            {
                foreach (var item in arr.OfType<JObject>())
                {
                    var g = item.Value<string>("Guid");
                    var k = item.Value<string>("Key");
                    if (!string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(k)) dynamic.Add((g, k));
                }
            }

            map[dir] = new FModelDirectoryConfig(
                GameDirectory: dir,
                GameName: v.Value<string>("GameName"),
                UeVersion: ToEnum<EGame>(v["UeVersion"]),
                TexturePlatform: ToEnum<ETexturePlatform>(v["TexturePlatform"]),
                AesMainKey: NullIfBlank(v["AesKeys"]?.Value<string>("MainKey")),
                AesDynamicKeys: dynamic,
                MappingsFilePath: FindMappingsPath(v["Endpoints"] as JArray));
        }
        return map;
    }

    /// <summary>FModel 把枚举存成整数；越界值一律丢弃而不是强转出无效枚举。</summary>
    private static T? ToEnum<T>(JToken? token) where T : struct, Enum
    {
        if (token is null || token.Type is JTokenType.Null) return null;
        try
        {
            var raw = token.Value<long>();
            var value = (T)Enum.ToObject(typeof(T), raw);
            return Enum.IsDefined(value) ? value : null;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindMappingsPath(JArray? endpoints)
    {
        // Endpoints[1] 按 FModel 的 EEndpointType 约定是 Mapping
        if (endpoints is null || endpoints.Count < 2) return null;
        var mapping = endpoints[1] as JObject;
        var overwrite = mapping?.Value<bool?>("Overwrite") ?? false;
        var filePath = NullIfBlank(mapping?.Value<string>("FilePath"));
        return overwrite && filePath is not null && File.Exists(filePath) ? filePath : null;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
