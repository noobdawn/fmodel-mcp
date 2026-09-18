using Microsoft.Data.Sqlite;

namespace FModel.Mcp.Indexing;

/// <summary>
/// SQLite 索引结构。所有可用于筛选/排序/聚合的指标都是**物化列**，
/// 派生值（如 tex_max_edge）在索引时算好，查询时不再计算。
/// </summary>
public static class IndexSchema
{
    /// <summary>schema 变更时递增，会触发索引重建。</summary>
    public const int Version = 1;

    public const string Ddl = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous  = NORMAL;

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- 每个 export 一行
        CREATE TABLE IF NOT EXISTS assets (
            id                INTEGER PRIMARY KEY,
            package_path      TEXT    NOT NULL,   -- Game/Content/Characters/Girl/girl009b/girl009b_body01_skm
            directory         TEXT    NOT NULL,   -- Game/Content/Characters/Girl/girl009b
            object_name       TEXT    NOT NULL,   -- girl009b_body01_skm
            class_name        TEXT    NOT NULL,   -- SkeletalMesh / Texture2D / AnimSequence ...
            export_index      INTEGER NOT NULL,
            file_size         INTEGER NOT NULL,   -- uasset+uexp+ubulk 合计

            -- Mesh -----------------------------------------------------------
            lod_count         INTEGER,
            triangles_lod0    INTEGER,
            triangles_all     INTEGER,
            vertices_lod0     INTEGER,
            bones             INTEGER,
            active_bones_lod0 INTEGER,
            morph_targets     INTEGER,
            material_slots    INTEGER,
            is_nanite         INTEGER,            -- 0/1
            triangles_nanite  INTEGER,
            vertices_nanite   INTEGER,

            -- Texture --------------------------------------------------------
            tex_width         INTEGER,
            tex_height        INTEGER,
            tex_max_edge      INTEGER,            -- max(w,h)，派生
            tex_exact_res     TEXT,               -- "512x512"，派生
            tex_format        TEXT,               -- PF_DXT1 ...
            tex_mips          INTEGER,
            tex_kind          TEXT,               -- 2d | 2d_array | cube | volume | virtual

            -- Animation ------------------------------------------------------
            anim_frames       INTEGER,
            anim_duration     REAL,               -- 秒
            anim_rate_scale   REAL,

            -- Audio ----------------------------------------------------------
            audio_duration    REAL,
            audio_channels    INTEGER,
            audio_sample_rate INTEGER,

            -- 质量标记 -------------------------------------------------------
            integrity         TEXT NOT NULL DEFAULT 'ok',  -- ok | suspect
            integrity_note    TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_assets_class     ON assets(class_name);
        CREATE INDEX IF NOT EXISTS ix_assets_dir       ON assets(directory);
        CREATE INDEX IF NOT EXISTS ix_assets_name      ON assets(object_name);
        CREATE INDEX IF NOT EXISTS ix_assets_pkg       ON assets(package_path);
        CREATE INDEX IF NOT EXISTS ix_assets_tri       ON assets(triangles_lod0);
        CREATE INDEX IF NOT EXISTS ix_assets_texedge   ON assets(tex_max_edge);
        CREATE INDEX IF NOT EXISTS ix_assets_integrity ON assets(integrity);

        -- 包级引用边（正向：src 引用 dst）
        CREATE TABLE IF NOT EXISTS refs (
            src_path TEXT NOT NULL,
            dst_path TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_refs_src ON refs(src_path);
        CREATE INDEX IF NOT EXISTS ix_refs_dst ON refs(dst_path);
        """;

    /// <summary>给 Agent 看的 schema 摘要 —— 附在 describe_dataset 里，它照着写 SQL。</summary>
    public const string HumanReadableSchema = """
        表 assets（每个 export 一行）
          package_path, directory, object_name, class_name, export_index, file_size
          -- Mesh:    lod_count, triangles_lod0, triangles_all, vertices_lod0, bones,
                      active_bones_lod0, morph_targets, material_slots,
                      is_nanite, triangles_nanite, vertices_nanite
          -- Texture: tex_width, tex_height, tex_max_edge, tex_exact_res, tex_format,
                      tex_mips, tex_kind ('2d'|'2d_array'|'cube'|'volume'|'virtual')
          -- Anim:    anim_frames, anim_duration(秒), anim_rate_scale
          -- Audio:   audio_duration(秒), audio_channels, audio_sample_rate
          -- 质量:    integrity ('ok'|'suspect'), integrity_note
                      ★ 聚合统计时建议加 WHERE integrity='ok'，suspect 行的数值不可信

        表 refs（包级引用边，src 引用 dst）
          src_path, dst_path

        注意：
          - 同一 package 可能有多个 export（多行），统计模型数时用 COUNT(DISTINCT package_path)
            或按 class_name 过滤
          - 面数分桶示例：
              CASE WHEN triangles_lod0 < 500 THEN '<500'
                   WHEN triangles_lod0 < 1000 THEN '500-999'
                   WHEN triangles_lod0 < 2000 THEN '1k-1.9k' ... END
          - 分位数示例（P90）：
              SELECT triangles_lod0 FROM assets WHERE class_name='StaticMesh' AND integrity='ok'
              ORDER BY triangles_lod0 LIMIT 1
              OFFSET (SELECT CAST(COUNT(*)*0.9 AS INT) FROM assets WHERE class_name='StaticMesh' AND integrity='ok')
        """;

    public static void Initialize(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }

    public static void SetMeta(SqliteConnection cn, string key, string value)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static string? GetMeta(SqliteConnection cn, string key)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
