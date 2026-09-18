# Probes

Standalone utilities that validate the MCP server without an IDE host. All connection
details are passed as CLI flags or environment variables — **no keys or game paths are
stored in this repository**.

## AclProbe (`AclProbe/`)

A/B probe for ACL-compressed animation parsing. It reproduces the failure where animations
that reference `/ACLPlugin/...` cannot resolve their bone-compression codec (so
`CompressedDataStructure` stays `NULL`), and verifies the fix (registering
`VirtualPaths["ACLPlugin"]`), optionally running a real export to validate the full
decompression pipeline.

```powershell
dotnet build AclProbe\AclProbe.csproj -c Release -p:CUE4PARSE_SKIP_NATIVE=true

# A) reproduce — no virtual-path registration -> CompressedDataStructure = NULL (exit 2)
aclprobe.exe --paks <PaksDir> --pkg <packagePath>

# B) verify — register the mapping -> structure parses, frames readable (exit 0)
aclprobe.exe --paks <PaksDir> --pkg <packagePath> --with-map

# C) full pipeline — also run a real export (validates ACL decompression)
aclprobe.exe --paks <PaksDir> --pkg <packagePath> --with-map --export
```

Optional flags: `--aes 0x…`, `--usmap <file>`, `--game GAME_XXX`, `--natives <dir>`
(point the last one at a local FModel `.data` folder if the game uses Oodle compression).
Every flag has an environment-variable counterpart (`FMODEL_PROBE_*`).

Exit codes: `0` parsed · `2` structure still `NULL` · `1` bad input / package not found · `3` exception.

## mcp_client.py

Minimal MCP stdio client (Python 3, no third-party dependencies). It speaks the protocol
directly, so it can drive the server when no IDE host is running:

```text
py mcp_client.py verify --paks <PaksDir> --pkg <packagePath>
py mcp_client.py count  --paks <PaksDir>
py mcp_client.py chunk 1 --paks <PaksDir> --chunk-dir <dir>
py mcp_client.py raw describe_dataset '{"sessionId": "s…"}'
```

Notes:

- `--exe` / `FMODEL_MCP_EXE` selects the server binary (defaults to
  `../FModel.Mcp/publish/fmodel-mcp.exe`).
- `--index` / `FMODEL_MCP_INDEX_DIR` is forwarded to the child process; otherwise the server
  falls back to its default index root (`%LocalAppData%\FModelMcp\index`).
- Child-process logs are captured to `%TEMP%\mcp_client_stderr.log`.
