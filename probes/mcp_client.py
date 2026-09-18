#!/usr/bin/env python3
"""Minimal MCP stdio client — drive fmodel-mcp (or any MCP stdio server) without an IDE host.

Tasks:
    verify [--pkg <packagePath>]   open_game + read_asset round-trip
    count                          open_game + animation integrity stats (query_index)
    chunk <N>                      open_game + index_paths(force) over chunkN.txt (see --chunk-dir)
    raw <tool> <jsonArgs>          call an arbitrary tool with JSON arguments
    help                           show this text

Configuration (CLI flag overrides environment variable; nothing is stored in this file):
    --exe        FMODEL_MCP_EXE          server executable
                                         (default: <repo>/FModel.Mcp/publish/fmodel-mcp.exe)
    --paks       FMODEL_PROBE_PAKS       game Paks directory (required for open_game)
    --game       FMODEL_PROBE_GAME       EGame preset (default: GAME_UE5_LATEST)
    --aes        FMODEL_PROBE_AES        AES main key "0x…" (optional)
    --usmap      FMODEL_PROBE_USMAP      .usmap / .jmap mappings file (optional)
    --scope      FMODEL_PROBE_SCOPE      comma-separated scope prefixes (optional)
    --index      FMODEL_MCP_INDEX_DIR    index root passed to the child process (optional)
    --chunk-dir  FMODEL_PROBE_CHUNK_DIR  directory containing chunkN.txt (default: ./chunks)
    --pkg        FMODEL_PROBE_PKG        package path for the verify task
"""
import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
STDERR_LOG = os.path.join(tempfile.gettempdir(), "mcp_client_stderr.log")


def flag(name, env, default=None):
    """CLI flag (with value) wins over environment variable; default if neither."""
    if name in sys.argv:
        i = sys.argv.index(name)
        if i + 1 < len(sys.argv):
            return sys.argv[i + 1]
    return os.environ.get(env, default)


EXE = flag("--exe", "FMODEL_MCP_EXE",
           os.path.abspath(os.path.join(HERE, "..", "FModel.Mcp", "publish", "fmodel-mcp.exe")))
PAKS = flag("--paks", "FMODEL_PROBE_PAKS")
GAME = flag("--game", "FMODEL_PROBE_GAME", "GAME_UE5_LATEST")
AES = flag("--aes", "FMODEL_PROBE_AES")
USMAP = flag("--usmap", "FMODEL_PROBE_USMAP")
SCOPE = flag("--scope", "FMODEL_PROBE_SCOPE")
INDEX = flag("--index", "FMODEL_MCP_INDEX_DIR")
CHUNK_DIR = flag("--chunk-dir", "FMODEL_PROBE_CHUNK_DIR", os.path.abspath(os.path.join(HERE, "chunks")))
PKG = flag("--pkg", "FMODEL_PROBE_PKG")

CHILD_ENV = dict(os.environ)
if INDEX:
    CHILD_ENV["FMODEL_MCP_INDEX_DIR"] = INDEX


def game_args():
    if not PAKS:
        sys.exit("missing --paks <PaksDir> (or FMODEL_PROBE_PAKS)")
    a = {"gameDirectory": PAKS, "ueVersion": GAME}
    if AES:
        a["aesMainKey"] = AES
    if USMAP:
        a["mappingsPath"] = USMAP
    if SCOPE:
        a["scope"] = [s.strip() for s in SCOPE.split(",") if s.strip()]
    return a


class Mcp:
    def __init__(self):
        self._err = open(STDERR_LOG, "w", encoding="utf-8", errors="replace")
        self.proc = subprocess.Popen(
            [EXE],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=self._err,
            text=True,
            encoding="utf-8",
            bufsize=1,
            env=CHILD_ENV,
        )
        self._id = 0
        self._handshake()

    def _send(self, obj):
        self.proc.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()

    def _recv(self, want_id):
        while True:
            line = self.proc.stdout.readline()
            if not line:
                tail = ""
                try:
                    self._err.flush()
                    with open(STDERR_LOG, encoding="utf-8", errors="replace") as f:
                        tail = "".join(f.readlines()[-8:])
                except Exception:
                    pass
                raise RuntimeError(f"MCP stdout closed. stderr tail:\n{tail}")
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == want_id:
                return msg

    def call(self, method, params):
        self._id += 1
        self._send({"jsonrpc": "2.0", "id": self._id, "method": method, "params": params})
        return self._recv(self._id)

    def notify(self, method, params=None):
        self._send({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def _handshake(self):
        r = self.call("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "mcp_client", "version": "0.1"},
        })
        if "error" in r:
            raise RuntimeError(f"initialize failed: {r['error']}")
        v = r.get("result", {}).get("protocolVersion")
        print(f"  [mcp] initialized, protocol={v}")
        self.notify("notifications/initialized")

    def tool(self, name, args):
        r = self.call("tools/call", {"name": name, "arguments": args})
        if "error" in r:
            raise RuntimeError(f"{name} -> JSON-RPC error: {r['error']}")
        res = r.get("result", {})
        content = res.get("content") or []
        text = content[0].get("text", "") if content else ""
        return bool(res.get("isError")), text

    def close(self):
        try:
            self.proc.terminate()
            self.proc.wait(timeout=10)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass
        finally:
            try:
                self._err.close()
            except Exception:
                pass


def open_game(m):
    ok, t = m.tool("open_game", game_args())
    try:
        og = json.loads(t)
    except Exception:
        raise RuntimeError(f"open_game returned non-JSON (isError={ok}): {t[:500]}")
    idx = og.get("index", {})
    print(f"  [mcp] open_game sid={og.get('sessionId')} reused={idx.get('reusedExisting')} "
          f"status={idx.get('status')} problems={og.get('problems')}")
    return og["sessionId"]


def main():
    task = sys.argv[1] if len(sys.argv) > 1 else "help"
    if task == "help" or task.startswith("-"):
        print(__doc__)
        return 0

    m = Mcp()
    try:
        sid = open_game(m)

        if task == "verify":
            if not PKG:
                sys.exit("verify requires --pkg <packagePath> (or FMODEL_PROBE_PKG)")
            ok, t = m.tool("read_asset", {"sessionId": sid, "packagePath": PKG, "maxBytes": 30000})
            print(f"  [mcp] read_asset isError={ok}")
            try:
                wrapper = json.loads(t)
                inner = json.loads(wrapper["json"]) if isinstance(wrapper.get("json"), str) else wrapper.get("json")
                obj = inner[0]
                props = obj.get("Properties", {})
                cds = obj.get("CompressedDataStructure")
                print(f"  [mcp] SequenceLength={props.get('SequenceLength')}  "
                      f"CompressedRawDataSize={obj.get('CompressedRawDataSize')}  "
                      f"CDS={'NULL' if cds is None else 'parsed'}")
                if isinstance(cds, dict):
                    print(f"  [mcp] CDS sample: {json.dumps(cds, ensure_ascii=False)[:300]}")
            except Exception as e:
                print(f"  [mcp] parse note: {e}")
                print(f"  RAW(preview): {t[:800]}")

        elif task == "count":
            sql = ("SELECT COUNT(DISTINCT package_path) AS anim_suspect, "
                   "SUM(CASE WHEN integrity<>'ok' THEN 1 ELSE 0 END) AS suspect_rows "
                   "FROM assets WHERE class_name='AnimSequence'")
            ok, t = m.tool("query_index", {"sessionId": sid, "sql": sql})
            print(f"  [mcp] count: {t}")

        elif task == "chunk":
            n = sys.argv[2]
            path = os.path.join(CHUNK_DIR, f"chunk{n}.txt")
            with open(path, encoding="utf-8-sig") as f:
                paths = [l.strip() for l in f if l.strip()]
            print(f"  chunk{n}: {len(paths)} paths loaded ({path})")
            t0 = time.time()
            ok, t = m.tool("index_paths", {"sessionId": sid, "packagePaths": paths, "force": True})
            dt = time.time() - t0
            print(f"  [mcp] index_paths done in {dt:.0f}s  isError={ok}")
            try:
                print(f"  [mcp] result: {json.dumps(json.loads(t), ensure_ascii=False)[:1200]}")
            except Exception:
                print(f"  RAW: {t[:800]}")

        elif task == "raw":
            name = sys.argv[2]
            payload = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
            ok, t = m.tool(name, payload)
            print(f"  [mcp] {name} isError={ok}")
            print(t[:4000])

        else:
            sys.exit(f"unknown task: {task}")
    finally:
        m.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
