# FModel MCP

**MCP server for Unreal Engine asset research** — mount a game, build a local SQLite index, query it with SQL, trace package references, and export models, textures, and animations, all from an AI agent. Built on CUE4Parse; no FModel patches, no GUI.

`FModel MCP` is a [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI agents first-class access to Unreal Engine game assets. Built on **CUE4Parse** — the asset-parsing library behind [FModel](https://github.com/4sval/FModel) — it mounts a game's `.pak` and IoStore containers, builds a local SQLite index of every package and export (meshes, textures, animations, audio, materials, and more), and exposes ten tools covering the full research loop: structure discovery, read-only SQL over the index, package-level reference graphs, JSON asset inspection, and multi-format export (UEFormat, glTF, ActorX, USD, PNG, raw bytes). Neither a GUI nor any modification to FModel or CUE4Parse is required. The server is designed agent-first: empty results come with an explanation, numeric metrics pass sanity checks, and dataset-integrity warnings surface before unreliable data can propagate. It has been verified end-to-end against two commercial titles, including projects with 700k+ files and indexes of over 2.2 million export records.

## Features

- **10 MCP tools** — `open_game`, `index_status`, `describe_dataset`, `list_dirs`, `query_index`, `find_references`, `expand_references`, `index_paths`, `read_asset`, `export_assets`
- **Scoped indexing** — seconds for a character folder, minutes for a full game (parallel scan + single-writer SQLite)
- **Read-only SQL over the asset index** — ideal for LLM-driven analysis; no query DSL to learn
- **Package-level reference graph** — forward edges always available, reverse graph built during indexing
- **Multi-format export** — UEFormat (`.uemodel`/`.ueanim`), glTF, ActorX (`.psk`/`.psa`), USD, textures, JSON properties, raw bytes
- **Broad game coverage** — UE4/UE5 titles, pak and IoStore containers, AES-encrypted archives, `.usmap`/`.jmap` mappings, game-specific quirks
- **Zero-patch design** — drop-in alongside an existing FModel checkout; reuses its native libraries and config (read-only)

## Repository layout

```
FModel.Mcp/        MCP server (.NET 10, stdio transport)
  Tools/           MCP tool implementations
  Indexing/        parallel scanner, metric extraction, SQLite schema, sanity checks
  Session/         game sessions, index reuse & invalidation logic
  Runtime/         native library loading, FModel config discovery, export path safety
probes/            standalone validation utilities (see probes/README.md)
```

## Requirements

- **Windows** (native-library handling currently targets Windows builds of FModel)
- **.NET 10 SDK**
- A local checkout of **FModel with its `CUE4Parse` submodule** — referenced as source at build time

## Building

```powershell
git clone https://github.com/noobdawn/fmodel-mcp.git
cd fmodel-mcp

# FModel checkout next to this repo (provides CUE4Parse source at build time
# and native libraries at runtime)
git clone --recursive https://github.com/4sval/FModel.git ..\FModel

# Build & publish
dotnet publish FModel.Mcp/FModel.Mcp.csproj -c Release -o FModel.Mcp/publish -p:CUE4PARSE_SKIP_NATIVE=true
```

Notes:

- `CUE4PARSE_SKIP_NATIVE=true` skips building CUE4Parse's native components (which would require CMake).
- Point the build at a different CUE4Parse checkout with `-p:CUE4ParseRoot=D:\path\to\CUE4Parse`.
- **Native libraries** (Oodle, zlib-ng, Detex, ACL) are never redistributed by this project. At runtime the server locates them automatically from a local FModel installation (`<OutputDirectory>\.data\` via FModel's `AppSettings.json`), from a single-file FModel extraction directory, or from its own folder.

## Configuring your MCP client

```json
{
  "mcp": {
    "fmodel": {
      "type": "local",
      "command": ["<path-to-repo>\\FModel.Mcp\\publish\\fmodel-mcp.exe"],
      "environment": {
        "FMODEL_MCP_INDEX_DIR": "D:\\FModelMcpIndex"
      }
    }
  }
}
```

- `FMODEL_MCP_INDEX_DIR` is optional — by default indexes live under `%LocalAppData%\FModelMcp\index`.
- Game path, UE version preset, AES key and `.usmap`/`.jmap` path are passed as `open_game` arguments at call time; nothing is hardcoded.

## Tools

| Tool | Purpose |
|---|---|
| `open_game` | Mount a game directory and start background indexing (optionally scoped) |
| `index_status` | Index progress, dataset integrity, IoStore diagnostics |
| `describe_dataset` | Asset organization overview + index SQL schema |
| `list_dirs` | Directory-tree explorer with pattern filter and multi-key sorting |
| `query_index` | Read-only SQL over the indexed assets (single `SELECT`/`WITH`) |
| `find_references` | Single-package references (live outgoing / indexed incoming) |
| `expand_references` | Batch reference expansion from multiple roots with class aggregation |
| `index_paths` | Incrementally index specific packages (breaks out of an index scope) |
| `read_asset` | Deserialize any asset to JSON (paginated / truncated) |
| `export_assets` | Export meshes, textures, animations and materials to files |

## Probes

Standalone utilities used to validate behavior without an IDE host:

- **`probes/AclProbe/`** — A/B probe for ACL-compressed animation parsing; verifies the `/ACLPlugin` virtual-path fix (structure `NULL` → parsed, optional real export)
- **`probes/mcp_client.py`** — minimal MCP stdio client; drives the server directly (`verify` / `count` / `chunk` / `raw` tasks)

Both take connection details via CLI flags or environment variables. **No keys or game paths are stored in this repository.**

## Known limitations

- The reverse reference graph supports legacy `.pak` archives only (IoStore `.utoc/.ucas` packages are mounted and indexed but contribute no reference edges)
- No 3D preview (meshes can be exported to `.glb` for external viewers)
- Behavior depends on CUE4Parse's per-title support; verified on a small number of titles so far

## License & Credits

[Apache-2.0](LICENSE) © 2026 noobdawn

This project contains no game content and never redistributes proprietary native libraries; they are loaded from a local FModel installation at runtime. Built on CUE4Parse (Apache-2.0). Not affiliated with FModel or Epic Games.
