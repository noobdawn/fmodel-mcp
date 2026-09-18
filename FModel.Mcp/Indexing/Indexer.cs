using System.Diagnostics;
using System.Threading.Channels;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Data.Sqlite;

namespace FModel.Mcp.Indexing;

public sealed record IndexOptions(
    IReadOnlyList<string> Scope,      // 路径前缀白名单；空 = 全量
    bool BuildRefGraph,
    int MaxDegreeOfParallelism);

/// <summary>
/// 全量/范围深度索引。单写线程 + 并行扫描，避免 SQLite 并发写。
/// 实测（尘白完整安装）：header 冷读约 6.9 ms/包，并行后约 1.9 ms/包；
/// 引用图走 ImportMap 廉价路径（0.28 ms/包），比 ResolvedObject 快 18 倍。
/// </summary>
public sealed class Indexer(AbstractVfsFileProvider provider, string dbPath, IndexOptions options)
{
    private readonly PackagePathNormalizer _paths = new(provider);

    /// <summary>建引用图时因 IoStore 格式而跳过的包数 —— 全空的引用图必须能解释原因。</summary>
    private int _ioStoreSkipped;
    public int IoStoreSkipped => Volatile.Read(ref _ioStoreSkipped);

    private volatile IndexProgress _progress = new("pending", 0, 0, 0, 0, 0);
    public IndexProgress Progress => _progress;

    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var targets = SelectTargets();
            _progress = new IndexProgress("scanning", 0, targets.Count, 0, 0, sw.Elapsed.TotalSeconds);

            var channel = Channel.CreateBounded<AssetBatch>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            var writer = Task.Run(() => WriteLoopAsync(channel.Reader, sw, targets.Count, ct), ct);
            await ScanAsync(targets, channel.Writer, ct).ConfigureAwait(false);
            channel.Writer.Complete();
            await writer.ConfigureAwait(false);

            var p = _progress;
            _progress = p with { Phase = "done", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (OperationCanceledException)
        {
            _progress = _progress with { Phase = "cancelled", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
        catch (Exception e)
        {
            _progress = _progress with { Phase = "failed", Error = $"{e.GetType().Name}: {e.Message}", ElapsedSeconds = sw.Elapsed.TotalSeconds };
        }
    }

    /// <summary>
    /// 增量索引：把指定的包补进已有索引。
    ///
    /// 存在的理由：scope 是主扫描的硬边界，但引用图经常指向 scope 之外的**共享资产**
    /// （实测 girl008b 的皮肤材质、Ramp、Matcap、IDMap 全在 Game/Content/Materials/Character/ 下），
    /// 这些包不补进索引就永远查不到，统计会系统性偏低。
    ///
    /// 幂等：默认跳过已索引的包；force=true 时先删后插重建。
    /// </summary>
    public async Task<IncrementalIndexResult> IndexPackagesAsync(
        IReadOnlyList<string> packagePaths, bool force, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var unresolved = new List<string>();

        var wanted = packagePaths
            .Select(p => _paths.Normalize(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var cn = new SqliteConnection($"Data Source={dbPath}");
        cn.Open();
        IndexSchema.Initialize(cn);

        var existing = force
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : QueryExisting(cn, wanted);

        var todo = new List<GameFile>();
        foreach (var path in wanted)
        {
            if (existing.Contains(path)) continue;
            if (TryResolveFile(path, out var file)) todo.Add(file);
            else unresolved.Add(path);
        }

        if (todo.Count == 0)
            return new IncrementalIndexResult(
                packagePaths.Count, wanted.Count, existing.Count, 0, unresolved.Count,
                0, 0, Math.Round(sw.Elapsed.TotalSeconds, 2), unresolved);

        var records = new List<AssetRecord>();
        var refs = new List<(string Src, string Dst)>();
        var failed = 0;
        var gate = new Lock();

        Parallel.ForEach(
            todo,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = ct },
            file =>
            {
                var (recs, rf) = ExtractOne(file);
                lock (gate)
                {
                    records.AddRange(recs);
                    refs.AddRange(rf);
                    if (recs.Count == 1 && recs[0].ExportIndex < 0) failed++;
                }
            });

        var touched = records.Select(r => r.PackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        using (var tx = cn.BeginTransaction())
        {
            DeleteByPackagePaths(cn, tx, touched);
            var suspect = 0;
            InsertAssets(cn, tx, records, ref suspect);
            if (refs.Count > 0) InsertRefs(cn, tx, refs);
            tx.Commit();
        }

        return new IncrementalIndexResult(
            packagePaths.Count, wanted.Count, existing.Count, touched.Count, unresolved.Count,
            records.Count, refs.Count, Math.Round(sw.Elapsed.TotalSeconds, 2), unresolved);
    }

    private bool TryResolveFile(string packagePath, out GameFile file)
    {
        foreach (var ext in new[] { ".uasset", ".umap" })
            if (provider.Files.TryGetValue(packagePath + ext, out var f)) { file = f; return true; }
        file = null!;
        return false;
    }

    /// <summary>分批查已索引的包，避免超长 IN 子句。</summary>
    private static HashSet<string> QueryExisting(SqliteConnection cn, List<string> paths)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int Chunk = 400;

        for (var offset = 0; offset < paths.Count; offset += Chunk)
        {
            var slice = paths.Skip(offset).Take(Chunk).ToList();
            using var cmd = cn.CreateCommand();
            var names = slice.Select((_, i) => $"$p{i}").ToList();
            cmd.CommandText = $"SELECT DISTINCT package_path FROM assets WHERE package_path IN ({string.Join(",", names)})";
            for (var i = 0; i < slice.Count; i++) cmd.Parameters.AddWithValue(names[i], slice[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) found.Add(r.GetString(0));
        }
        return found;
    }

    private static void DeleteByPackagePaths(SqliteConnection cn, SqliteTransaction tx, List<string> paths)
    {
        using var del = cn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM assets WHERE package_path = $p";
        var p1 = del.Parameters.Add(new SqliteParameter("$p", ""));

        using var delRefs = cn.CreateCommand();
        delRefs.Transaction = tx;
        delRefs.CommandText = "DELETE FROM refs WHERE src_path = $p";
        var p2 = delRefs.Parameters.Add(new SqliteParameter("$p", ""));

        foreach (var path in paths)
        {
            p1.Value = path;
            del.ExecuteNonQuery();
            p2.Value = path;
            delRefs.ExecuteNonQuery();
        }
    }

    private List<GameFile> SelectTargets()    {
        var q = provider.Files.Values.Where(f =>
            f.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) ||
            f.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase));

        q = q.Where(f => !f.IsUePackagePayload);

        if (options.Scope.Count > 0)
            q = q.Where(f => options.Scope.Any(s => f.Path.StartsWith(s, StringComparison.OrdinalIgnoreCase)));

        return q.ToList();
    }

    private sealed record AssetBatch(List<AssetRecord> Assets, List<(string Src, string Dst)> Refs, int Packages);

    private async Task ScanAsync(List<GameFile> targets, ChannelWriter<AssetBatch> sink, CancellationToken ct)
    {
        const int BatchSize = 512;
        var pending = new List<AssetRecord>(BatchSize);
        var pendingRefs = new List<(string, string)>(BatchSize * 6);
        var pendingPackages = 0;
        var gate = new Lock();

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var (records, refs) = ExtractOne(file);
                List<AssetRecord>? flushAssets = null;
                List<(string, string)>? flushRefs = null;
                var flushPackages = 0;

                lock (gate)
                {
                    pending.AddRange(records);
                    pendingRefs.AddRange(refs);
                    pendingPackages++;                 // ★ 进度以「包」为单位，与 total 对齐
                    if (pending.Count >= BatchSize)
                    {
                        flushAssets = [.. pending];
                        flushRefs = [.. pendingRefs];
                        flushPackages = pendingPackages;
                        pending.Clear();
                        pendingRefs.Clear();
                        pendingPackages = 0;
                    }
                }

                if (flushAssets is not null && flushRefs is not null)
                    await sink.WriteAsync(new AssetBatch(flushAssets, flushRefs, flushPackages), token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (pending.Count > 0 || pendingRefs.Count > 0 || pendingPackages > 0)
            await sink.WriteAsync(new AssetBatch([.. pending], [.. pendingRefs], pendingPackages), ct).ConfigureAwait(false);
    }

    private (List<AssetRecord> Records, List<(string Src, string Dst)> Refs) ExtractOne(GameFile file)
    {
        var records = new List<AssetRecord>(2);
        var refs = new List<(string, string)>();
        var packagePath = file.PathWithoutExtension;

        IPackage pkg;
        try
        {
            pkg = provider.LoadPackage(file);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            records.Add(MakeFailed(file, packagePath, $"LoadPackage 失败: {e.GetType().Name}"));
            return (records, refs);
        }

        // ── 引用边：只读 ImportMap 字段，不走 ResolvedObject（快 18 倍） ──
        // import 表里是 UE 虚拟路径（/Game/...），必须规范化成索引用的挂载点路径，
        // 否则 dst_path 与 package_path 对不上，反向查询永远查不到东西。
        if (options.BuildRefGraph)
        {
            if (pkg is Package legacy)
            {
                foreach (var import in legacy.ImportMap)
                {
                    if (!import.ClassName.Text.Equals("Package", StringComparison.Ordinal)) continue;
                    var raw = import.ObjectName.Text;
                    if (string.IsNullOrEmpty(raw)) continue;

                    var dst = _paths.Normalize(raw);
                    if (!string.IsNullOrEmpty(dst) && !dst.Equals(packagePath, StringComparison.OrdinalIgnoreCase))
                        refs.Add((packagePath, dst));
                }
            }
            else
            {
                // IoStore 的 IoPackage 用 FPackageObjectIndex 而非 FObjectImport，当前不支持。
                // 必须计数并上报 —— 否则整个引用图是空的却没人知道为什么。
                Interlocked.Increment(ref _ioStoreSkipped);
            }
        }

        var totalSize = MeasurePayload(file);

        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            string className;
            try
            {
                className = new FPackageIndex(pkg, i + 1).ResolvedObject?.Class?.Name.Text ?? "Unknown";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                className = "Unknown";
            }

            var rec = new AssetRecord
            {
                PackagePath = packagePath,
                Directory = DirectoryOf(packagePath),
                ObjectName = NameOf(pkg, i, file),
                ClassName = className,
                ExportIndex = i,
                FileSize = totalSize
            };

            if (MetricExtractor.NeedsDeserialization(className))
            {
                try
                {
                    MetricExtractor.Fill(rec, pkg.ExportsLazy[i].Value);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    rec.MarkSuspect($"反序列化失败: {e.GetType().Name}");
                }
            }

            Sanity.Validate(rec);
            records.Add(rec);
        }

        return (records, refs);
    }

    private static string NameOf(IPackage pkg, int index, GameFile file)
    {
        try
        {
            var ro = new FPackageIndex(pkg, index + 1).ResolvedObject;
            var n = ro?.Name.Text;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* fallthrough */ }
        return file.NameWithoutExtension;
    }

    private long MeasurePayload(GameFile file)
    {
        try
        {
            provider.Files.FindPayloads(file, out var uexp, out var ubulks, out _);
            return file.Size + (uexp?.Size ?? 0) + ubulks.Sum(b => b.Size);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return file.Size;
        }
    }

    private static AssetRecord MakeFailed(GameFile file, string packagePath, string note)
    {
        var r = new AssetRecord
        {
            PackagePath = packagePath,
            Directory = DirectoryOf(packagePath),
            ObjectName = file.NameWithoutExtension,
            ClassName = "Unknown",
            ExportIndex = -1,
            FileSize = file.Size
        };
        r.MarkSuspect(note);
        return r;
    }

    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : string.Empty;
    }

    // ────────────────────────────── 写入 ──────────────────────────────

    private async Task WriteLoopAsync(ChannelReader<AssetBatch> source, Stopwatch sw, int total, CancellationToken ct)
    {
        await using var cn = new SqliteConnection($"Data Source={dbPath}");
        cn.Open();
        IndexSchema.Initialize(cn);

        var processed = 0;
        var suspect = 0;
        long edges = 0;

          await foreach (var batch in source.ReadAllAsync(ct).ConfigureAwait(false))
          {
              using var tx = cn.BeginTransaction();
              // 幂等重写：先清掉这批包的既有行与旧引用边，再插入。
              // 全量扫描也可能跑在既有库上（例如索引文件被占用、删库被吞掉），不先删就会产生重复行。
              if (batch.Assets.Count > 0 || batch.Refs.Count > 0)
              {
                  var touched = batch.Assets.Select(a => a.PackagePath)
                      .Concat(batch.Refs.Select(r => r.Src))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
                  DeleteByPackagePaths(cn, tx, touched);
              }
              InsertAssets(cn, tx, batch.Assets, ref suspect);
              if (batch.Refs.Count > 0) { InsertRefs(cn, tx, batch.Refs); edges += batch.Refs.Count; }
              tx.Commit();

            processed += batch.Packages;
            _progress = new IndexProgress("scanning", processed, total, suspect, edges, sw.Elapsed.TotalSeconds);
        }

        using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "ANALYZE";
            cmd.ExecuteNonQuery();
        }

        _progress = new IndexProgress("writing", processed, total, suspect, edges, sw.Elapsed.TotalSeconds);
    }

    private static void InsertAssets(SqliteConnection cn, SqliteTransaction tx, List<AssetRecord> rows, ref int suspect)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO assets(
              package_path,directory,object_name,class_name,export_index,file_size,
              lod_count,triangles_lod0,triangles_all,vertices_lod0,bones,active_bones_lod0,
              morph_targets,material_slots,is_nanite,triangles_nanite,vertices_nanite,
              tex_width,tex_height,tex_max_edge,tex_exact_res,tex_format,tex_mips,tex_kind,
              anim_frames,anim_duration,anim_rate_scale,
              audio_duration,audio_channels,audio_sample_rate,
              integrity,integrity_note)
            VALUES(
              $pkg,$dir,$name,$cls,$eidx,$size,
              $lod,$tri0,$triAll,$vtx0,$bones,$abones,
              $morph,$mats,$nanite,$triN,$vtxN,
              $tw,$th,$tmax,$tres,$tfmt,$tmips,$tkind,
              $af,$ad,$ars,
              $aud,$ach,$asr,
              $integ,$note)
            """;

        var p = cmd.Parameters;
        foreach (var name in new[] {
            "$pkg","$dir","$name","$cls","$eidx","$size","$lod","$tri0","$triAll","$vtx0","$bones","$abones",
            "$morph","$mats","$nanite","$triN","$vtxN","$tw","$th","$tmax","$tres","$tfmt","$tmips","$tkind",
            "$af","$ad","$ars","$aud","$ach","$asr","$integ","$note" })
            p.Add(new SqliteParameter(name, DBNull.Value));

        foreach (var r in rows)
        {
            if (r.Integrity != "ok") suspect++;
            p["$pkg"].Value = r.PackagePath;
            p["$dir"].Value = r.Directory;
            p["$name"].Value = r.ObjectName;
            p["$cls"].Value = r.ClassName;
            p["$eidx"].Value = r.ExportIndex;
            p["$size"].Value = r.FileSize;
            p["$lod"].Value = Box(r.LodCount);
            p["$tri0"].Value = Box(r.TrianglesLod0);
            p["$triAll"].Value = Box(r.TrianglesAll);
            p["$vtx0"].Value = Box(r.VerticesLod0);
            p["$bones"].Value = Box(r.Bones);
            p["$abones"].Value = Box(r.ActiveBonesLod0);
            p["$morph"].Value = Box(r.MorphTargets);
            p["$mats"].Value = Box(r.MaterialSlots);
            p["$nanite"].Value = r.IsNanite is null ? DBNull.Value : (r.IsNanite.Value ? 1 : 0);
            p["$triN"].Value = Box(r.TrianglesNanite);
            p["$vtxN"].Value = Box(r.VerticesNanite);
            p["$tw"].Value = Box(r.TexWidth);
            p["$th"].Value = Box(r.TexHeight);
            p["$tmax"].Value = Box(r.TexMaxEdge);
            p["$tres"].Value = Box(r.TexExactRes);
            p["$tfmt"].Value = Box(r.TexFormat);
            p["$tmips"].Value = Box(r.TexMips);
            p["$tkind"].Value = Box(r.TexKind);
            p["$af"].Value = Box(r.AnimFrames);
            p["$ad"].Value = Box(r.AnimDuration);
            p["$ars"].Value = Box(r.AnimRateScale);
            p["$aud"].Value = Box(r.AudioDuration);
            p["$ach"].Value = Box(r.AudioChannels);
            p["$asr"].Value = Box(r.AudioSampleRate);
            p["$integ"].Value = r.Integrity;
            p["$note"].Value = Box(r.IntegrityNote);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertRefs(SqliteConnection cn, SqliteTransaction tx, List<(string Src, string Dst)> rows)
    {
        using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO refs(src_path,dst_path) VALUES($s,$d)";
        var s = cmd.Parameters.Add(new SqliteParameter("$s", ""));
        var d = cmd.Parameters.Add(new SqliteParameter("$d", ""));
        foreach (var (src, dst) in rows)
        {
            s.Value = src;
            d.Value = dst;
            cmd.ExecuteNonQuery();
        }
    }

    private static object Box<T>(T? v) where T : struct => v.HasValue ? v.Value : DBNull.Value;
    private static object Box(string? v) => v is null ? DBNull.Value : v;
}
