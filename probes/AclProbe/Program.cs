// AclProbe — standalone A/B probe for ACL-compressed animation parsing.
//
// It verifies the "/ACLPlugin virtual path" behavior end to end:
//   without --with-map : plugin references cannot resolve        -> CompressedDataStructure = NULL
//   with    --with-map : VirtualPaths["ACLPlugin"] is registered -> structure parses (frames readable)
//   add --export       : also run a real export to validate the full decompression pipeline.
//
// Usage:
//   aclprobe --paks <PaksDir> --pkg <packagePath> [options]
//
// Options (CLI flag overrides environment variable):
//   --paks    <dir>   game Paks directory                    (env: FMODEL_PROBE_PAKS)
//   --pkg     <path>  package path inside the mount          (env: FMODEL_PROBE_PKG)
//   --aes     <key>   AES main key "0x…", optional           (env: FMODEL_PROBE_AES)
//   --usmap   <file>  .usmap / .jmap mappings, optional      (env: FMODEL_PROBE_USMAP)
//   --game    <name>  EGame preset (default GAME_UE5_LATEST) (env: FMODEL_PROBE_GAME)
//   --natives <dir>   folder with oo2core_9_win64.dll etc.   (env: FMODEL_PROBE_NATIVES)
//   --with-map        register VirtualPaths["ACLPlugin"]     (env: FMODEL_PROBE_WITH_MAP=1)
//   --export          run a real export of the animation     (env: FMODEL_PROBE_EXPORT=1)
//
// Exit codes: 0 parsed · 2 structure still NULL · 1 bad input / not found · 3 exception

using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;

string? Flag(string name, string env)
{
    var i = Array.IndexOf(args, name);
    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    return Environment.GetEnvironmentVariable(env);
}

bool Toggle(string name, string env) =>
    args.Contains(name) || string.Equals(Environment.GetEnvironmentVariable(env), "1", StringComparison.Ordinal);

var paks = Flag("--paks", "FMODEL_PROBE_PAKS");
var pkgPath = Flag("--pkg", "FMODEL_PROBE_PKG");
var aes = Flag("--aes", "FMODEL_PROBE_AES");
var usmap = Flag("--usmap", "FMODEL_PROBE_USMAP");
var natives = Flag("--natives", "FMODEL_PROBE_NATIVES");
var gameName = Flag("--game", "FMODEL_PROBE_GAME") ?? "GAME_UE5_LATEST";
var withMap = Toggle("--with-map", "FMODEL_PROBE_WITH_MAP");
var withExport = Toggle("--export", "FMODEL_PROBE_EXPORT");

if (string.IsNullOrWhiteSpace(paks) || string.IsNullOrWhiteSpace(pkgPath))
{
    Console.WriteLine("usage: aclprobe --paks <PaksDir> --pkg <packagePath>");
    Console.WriteLine("               [--aes 0x…] [--usmap <file>] [--game GAME_XXX] [--natives <dir>]");
    Console.WriteLine("               [--with-map] [--export]");
    return 1;
}

if (!Enum.TryParse<EGame>(gameName, ignoreCase: true, out var game))
{
    Console.WriteLine($"unknown EGame preset: {gameName}");
    return 1;
}

try
{
    // Native libraries (Oodle / zlib / ACL) — never bundled; reuse them from a local FModel
    // install via --natives, or rely on DLLs already sitting next to this executable.
    foreach (var dll in new[] { "oo2core_9_win64.dll", "zlib-ng2.dll", "CUE4Parse-Natives.dll" })
    {
        var dst = Path.Combine(AppContext.BaseDirectory, dll);
        if (File.Exists(dst)) continue;
        if (natives is not null && File.Exists(Path.Combine(natives, dll)))
            File.Copy(Path.Combine(natives, dll), dst, overwrite: false);
    }

    try
    {
        OodleHelper.Initialize(Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_OLD));
        ZlibHelper.Initialize(Path.Combine(AppContext.BaseDirectory, ZlibHelper.DLL_NAME));
    }
    catch (Exception e)
    {
        Console.WriteLine($"[warn] native library init failed ({e.GetType().Name}): {e.Message}");
        Console.WriteLine("[warn] pass --natives <dir> pointing at FModel's .data folder if the game uses Oodle.");
    }

    var provider = new DefaultFileProvider(paks, SearchOption.AllDirectories,
        new VersionContainer(game, ETexturePlatform.DesktopMobile),
        StringComparer.OrdinalIgnoreCase)
    {
        ReadNaniteData = true
    };
    provider.Initialize();
    if (!string.IsNullOrWhiteSpace(aes))
        provider.SubmitKey(new FGuid(0U), new FAesKey(aes));
    provider.PostMount();
    if (!string.IsNullOrWhiteSpace(usmap) && File.Exists(usmap))
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);

    if (withMap)
    {
        // The experiment under test: register the plugin mount point so /ACLPlugin/… resolves
        // to the engine plugin directory (mirrors the MCP server's implementation, guard rails included).
        if (!provider.VirtualPaths.ContainsKey("ACLPlugin") &&
            provider.Files.Keys.Any(k => k.StartsWith("Engine/Plugins/Animation/ACLPlugin/Content/", StringComparison.OrdinalIgnoreCase)))
        {
            provider.VirtualPaths["ACLPlugin"] = "Engine/Plugins/Animation/ACLPlugin";
        }
    }

    Console.WriteLine($"withMap={withMap}  mounted={provider.MountedVfs.Count}  files={provider.Files.Count}");

    if (!provider.Files.TryGetValue(pkgPath + ".uasset", out var gf))
    {
        Console.WriteLine($"RESULT: package not found in mount: {pkgPath}");
        return 1;
    }

    var pkg = provider.LoadPackage(gf);

    UAnimSequence? anim = null;
    for (var i = 0; i < pkg.ExportsLazy.Length && anim is null; i++)
        if (pkg.ExportsLazy[i].Value is UAnimSequence a) anim = a;

    if (anim is null) { Console.WriteLine("RESULT: AnimSequence export NOT FOUND"); return 1; }

    var cds = anim.CompressedDataStructure;
    Console.WriteLine($"CompressedDataStructure = {(cds is null ? "NULL" : cds.GetType().Name)}");
    Console.WriteLine($"NumFrames               = {anim.NumFrames}");
    Console.WriteLine($"SequenceLength          = {anim.SequenceLength}");
    Console.WriteLine($"BoneCodecDDCHandle      = {anim.BoneCodecDDCHandle}");
    if (cds is not null)
        Console.WriteLine($"CompressedNumberOfFrames= {cds.CompressedNumberOfFrames}");

    if (withExport && cds is not null)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "aclprobe_out");
        Directory.CreateDirectory(outDir);
        var session = new ExportSession { MaxDegreeOfParallelism = 2 };
        session.Add(anim);
        var results = session.RunAsync(outDir, new ExportOptions(), null, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var r in results)
            Console.WriteLine($"EXPORT success={r.Success} files=[{string.Join(", ", r.DiskFilePaths ?? [])}] err={r.Error?.Message}");
    }

    return cds is null ? 2 : 0;
}
catch (Exception e)
{
    Console.WriteLine($"EXCEPTION: {e.GetType().Name}: {e.Message}");
    return 3;
}
