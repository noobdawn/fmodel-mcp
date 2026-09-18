using System.Runtime.InteropServices;
using CUE4Parse.Compression;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Textures.BC;

namespace FModel.Mcp.Runtime;

/// <summary>
/// 定位并初始化 CUE4Parse 需要的原生库。
/// 优先复用本机 FModel 的 <c>&lt;Output&gt;\.data\</c> 目录 —— 实测这样能得到与 FModel 完全一致的解析/导出行为。
/// </summary>
public sealed record NativeLibraryStatus(
    bool Oodle,
    bool Zlib,
    bool Detex,
    bool Acl,
    IReadOnlyList<string> Notes)
{
    public bool AllCritical => Oodle && Zlib;
}

public static class NativeLibraries
{
    private static NativeLibraryStatus? _status;
    private static readonly Lock Gate = new();

    /// <summary>幂等；重复调用返回首次结果。</summary>
    public static NativeLibraryStatus EnsureLoaded(string? explicitNativeDir = null)
    {
        lock (Gate)
        {
            if (_status is not null) return _status;

            var notes = new List<string>();
            var target = AppContext.BaseDirectory;
            var sources = BuildSearchPaths(explicitNativeDir, notes);

            CopyIfFound("oo2core_9_win64.dll", sources, target, notes);
            CopyIfFound("zlib-ng2.dll", sources, target, notes);
            CopyIfFound("Detex.dll", sources, target, notes);
            CopyIfFound("CUE4Parse-Natives.dll", sources, target, notes);

            var oodle = TryInit("Oodle", notes,
                () => OodleHelper.Initialize(Path.Combine(target, OodleHelper.OODLE_NAME_OLD)),
                () => OodleHelper.Instance is not null);

            var zlib = TryInit("Zlib", notes,
                () => ZlibHelper.Initialize(Path.Combine(target, ZlibHelper.DLL_NAME)),
                () => ZlibHelper.Instance is not null);

            var detex = TryInit("Detex", notes,
                () => DetexHelper.Initialize(Path.Combine(target, DetexHelper.DLL_NAME)),
                () => true);

            var acl = CUE4ParseNatives.IsInitialized;
            if (!acl) notes.Add("CUE4Parse-Natives 未加载：ACL 压缩的动画将无法解析。");

            _status = new NativeLibraryStatus(oodle, zlib, detex, acl, notes);
            return _status;
        }
    }

    private static List<string> BuildSearchPaths(string? explicitDir, List<string> notes)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDir)) paths.Add(explicitDir);

        // FModel 的 <OutputDirectory>\.data\ —— 从它的 AppSettings.json 里取
        var fmData = FModelConfig.TryLoad()?.DataDirectory;
        if (fmData is not null && Directory.Exists(fmData))
        {
            paths.Add(fmData);
            notes.Add($"复用 FModel 原生库目录: {fmData}");
        }

        // FModel 单文件发布的解包目录里有 CUE4Parse-Natives.dll
        var extractRoot = Path.Combine(Path.GetTempPath(), ".net", "FModel");
        if (Directory.Exists(extractRoot))
        {
            try
            {
                paths.AddRange(Directory
                    .EnumerateFiles(extractRoot, "CUE4Parse-Natives.dll", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .OfType<string>());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 临时目录可能被清理/占用，忽略
            }
        }

        paths.Add(AppContext.BaseDirectory);
        return paths;
    }

    private static void CopyIfFound(string dll, IEnumerable<string> sources, string target, List<string> notes)
    {
        var dst = Path.Combine(target, dll);
        if (File.Exists(dst)) return;

        foreach (var dir in sources)
        {
            var src = Path.Combine(dir, dll);
            if (!File.Exists(src)) continue;
            try
            {
                File.Copy(src, dst, overwrite: false);
                return;
            }
            catch (IOException)
            {
                return; // 已被并发进程写入，视为成功
            }
            catch (UnauthorizedAccessException e)
            {
                notes.Add($"{dll} 复制失败: {e.Message}");
                return;
            }
        }

        notes.Add($"未找到 {dll}");
    }

    private static bool TryInit(string name, List<string> notes, Action init, Func<bool> verify)
    {
        try
        {
            init();
            var ok = verify();
            if (!ok) notes.Add($"{name} 初始化后实例为空");
            return ok;
        }
        catch (Exception e) when (e is DllNotFoundException or FileNotFoundException or BadImageFormatException or IOException)
        {
            notes.Add($"{name} 初始化失败: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    public static string Describe()
    {
        var s = _status;
        if (s is null) return "未初始化";
        return $"Oodle={s.Oodle} Zlib={s.Zlib} Detex={s.Detex} ACL={s.Acl} " +
               $"({RuntimeInformation.ProcessArchitecture})";
    }
}
