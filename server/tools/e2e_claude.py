"""End-to-end: real Claude Code CLI as the MCP client -> logging proxy -> ported server -> synthetic Unity peers.

Records which protocol Claude Code speaks (headers / _meta) and whether it can drive one and two
Unity instances through the ported server. Usage: python e2e_claude.py <server-src-or-exe> <outdir>
"""
from __future__ import annotations

import asyncio
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).parent))
from live_harness import UnityPeer  # noqa: E402

SRC = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve()
OUT.mkdir(parents=True, exist_ok=True)
PY = sys.executable


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


async def wait_health(base: str) -> bool:
    async with httpx.AsyncClient() as hc:
        for _ in range(100):
            try:
                if (await hc.get(base + "/health", timeout=2)).status_code == 200:
                    return True
            except Exception:
                pass
            await asyncio.sleep(0.2)
    return False


def run_claude(prompt: str, cfg: Path, outfile: Path, max_turns: int = 8) -> dict:
    cmd = ["claude", "-p", prompt, "--mcp-config", str(cfg), "--strict-mcp-config",
           "--allowedTools", "mcp__unityMCP", "--max-turns", str(max_turns), "--output-format", "json"]
    t0 = time.time()
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=600, cwd=OUT)
    outfile.write_text(f"$ {' '.join(cmd)}\n--- stdout\n{r.stdout}\n--- stderr\n{r.stderr}\n")
    try:
        body = json.loads(r.stdout)
    except Exception:
        body = {"raw": r.stdout[:2000]}
    return {"exit": r.returncode, "seconds": round(time.time() - t0, 1), "result": body.get("result") if isinstance(body, dict) else None,
            "num_turns": body.get("num_turns") if isinstance(body, dict) else None, "stderr": r.stderr[-800:]}


def summarize_proxy_log(path: Path) -> dict:
    text = path.read_bytes().decode("utf-8", errors="replace") if path.exists() else ""
    import re
    headers = sorted(set(re.findall(r"(?im)^(mcp-[a-z-]+|accept|user-agent|x-[a-z-]+):\s*(.*)$", text)))
    meta_keys = sorted(set(re.findall(r'"(io\.modelcontextprotocol/[A-Za-z]+)"', text)))
    methods = re.findall(r'"method":\s*"([^"]+)"', text)
    from collections import Counter
    return {
        "request_headers_seen": [f"{k}: {v}" for k, v in headers if not k.lower().startswith("accept")][:30],
        "meta_keys_seen": meta_keys,
        "methods": dict(Counter(methods)),
        "has_client_id_meta": '"client_id"' in text,
        "has_mcp_session_id": "mcp-session-id" in text.lower(),
    }


async def main() -> int:
    srv_port, proxy_port = free_port(), free_port()
    base = f"http://127.0.0.1:{srv_port}"
    via_proxy = f"http://127.0.0.1:{proxy_port}"
    env = {**os.environ, "UNITY_MCP_TELEMETRY_ENABLED": "0", "UNITY_MCP_SKIP_STARTUP_CONNECT": "1",
           "FASTMCP_SHOW_SERVER_BANNER": "false"}
    srv_log = (OUT / "server.log").open("w")
    if SRC.is_dir():
        cmd, cwd = [PY, "main.py", "--transport", "http", "--http-url", base], SRC
    else:
        cmd, cwd = [str(SRC), "--transport", "http", "--http-url", base], SRC.parent
    srv = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=srv_log, stderr=subprocess.STDOUT)
    proxy_log = OUT / "proxy.log"
    proxy_log.unlink(missing_ok=True)
    proxy = subprocess.Popen([PY, str(Path(__file__).with_name("logproxy.py")), str(proxy_port), str(srv_port), str(proxy_log)])
    peers: list[UnityPeer] = []
    report: dict = {}
    try:
        if not await wait_health(base):
            print("server did not start"); return 1
        cfg = OUT / "mcp.json"
        cfg.write_text(json.dumps({"mcpServers": {"unityMCP": {"type": "http", "url": via_proxy + "/mcp"}}}, indent=2))

        a = UnityPeer(base, "ProjA", "aaaa1111"); peers.append(a)
        await a.connect(); await a.register_tools(); await asyncio.sleep(0.5)

        # Scenario 1: one Unity, plain tool call.
        r1 = await asyncio.to_thread(run_claude,
            "Using the unityMCP MCP server, call the read_console tool with action=\"get\" exactly once and reply with the "
            "value of the 'message' field from the tool result, verbatim, and nothing else.",
            cfg, OUT / "claude-scenario1.txt", 4)
        report["scenario1_single_unity"] = {**r1, "peer_commands": [m["name"] for m in a.received]}
        print("scenario1:", json.dumps(report["scenario1_single_unity"], ensure_ascii=False)[:600], flush=True)

        # Scenario 2: two Unities, target the second one.
        b = UnityPeer(base, "ProjB", "bbbb2222"); peers.append(b)
        await b.connect(); await b.register_tools(); await asyncio.sleep(0.5)
        a_before, b_before = len(a.received), len(b.received)
        r2 = await asyncio.to_thread(run_claude,
            "Two Unity Editors are connected to the unityMCP MCP server. Call the read_console tool with action=\"get\" "
            "on the Unity instance whose project is named ProjB (its id is ProjB@bbbb2222). If the server answers that an "
            "instance selection is required, follow the hint in its response. Reply with the 'message' field of the "
            "successful read_console result, verbatim, and nothing else.",
            cfg, OUT / "claude-scenario2.txt", 8)
        report["scenario2_two_unities"] = {**r2, "peerA_new_commands": [m["name"] for m in a.received[a_before:]],
                                           "peerB_new_commands": [m["name"] for m in b.received[b_before:]],
                                           "peerB_params": [m.get("params") for m in b.received[b_before:]][:2]}
        print("scenario2:", json.dumps(report["scenario2_two_unities"], ensure_ascii=False)[:800], flush=True)

        # Scenario 3: the model is steered into set_active_instance first; it must recover from the
        # no_persistent_session hint and route per call.
        a_before, b_before = len(a.received), len(b.received)
        r3 = await asyncio.to_thread(run_claude,
            "Two Unity Editors are connected to the unityMCP MCP server: ProjA@aaaa1111 and ProjB@bbbb2222. "
            "Step 1: call the set_active_instance tool with instance=\"ProjB@bbbb2222\". Step 2: whatever it answers, "
            "obtain a successful read_console result (action=\"get\") from ProjB, following any hint the server gives. "
            "Reply with the 'message' field of that successful read_console result, verbatim, and nothing else.",
            cfg, OUT / "claude-scenario3.txt", 8)
        report["scenario3_set_active_first"] = {**r3, "peerA_new_commands": [m["name"] for m in a.received[a_before:]],
                                                "peerB_new_commands": [m["name"] for m in b.received[b_before:]]}
        print("scenario3:", json.dumps(report["scenario3_set_active_first"], ensure_ascii=False)[:800], flush=True)

        # Scenario 4: no ids given; the model must discover instances itself.
        a_before, b_before = len(a.received), len(b.received)
        r4 = await asyncio.to_thread(run_claude,
            "Two Unity Editors are connected to the unityMCP MCP server. Find out which Unity instances are connected "
            "(for example by reading the mcpforunity://instances resource, or from the server's error hints), then call "
            "read_console with action=\"get\" on the instance whose project name is ProjA. Reply with the 'message' field "
            "of that successful result, verbatim, and nothing else.",
            cfg, OUT / "claude-scenario4.txt", 10)
        report["scenario4_discover"] = {**r4, "peerA_new_commands": [m["name"] for m in a.received[a_before:]],
                                        "peerB_new_commands": [m["name"] for m in b.received[b_before:]]}
        print("scenario4:", json.dumps(report["scenario4_discover"], ensure_ascii=False)[:800], flush=True)
    finally:
        for p in peers:
            await p.close()
        proxy.terminate(); srv.terminate()
        try:
            srv.wait(10)
        except Exception:
            srv.kill()
        srv_log.close()
        report["wire"] = summarize_proxy_log(proxy_log)
        report["server_log_tracebacks"] = "Traceback" in (OUT / "server.log").read_text()
        (OUT / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False))
        print("wire:", json.dumps(report["wire"], ensure_ascii=False)[:1200])
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
