"""Transparent TCP proxy that logs plain-HTTP request lines, headers and bodies.

Used to see exactly what Claude Code sends to the MCP server (protocol version headers,
_meta envelope, session ids). Usage: python logproxy.py <listen_port> <target_port> <logfile>
"""
from __future__ import annotations

import asyncio
import sys
import time

LISTEN, TARGET, LOGFILE = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
lock = asyncio.Lock()


async def log(direction: str, cid: int, data: bytes) -> None:
    async with lock:
        with open(LOGFILE, "ab") as f:
            f.write(f"\n===== {time.strftime('%H:%M:%S')} conn={cid} {direction} {len(data)}B =====\n".encode())
            f.write(data[:6000])
            f.write(b"\n")


async def pump(reader: asyncio.StreamReader, writer: asyncio.StreamWriter, direction: str, cid: int) -> None:
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            await log(direction, cid, data)
            writer.write(data)
            await writer.drain()
    except Exception:
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


counter = 0


async def handle(client_reader: asyncio.StreamReader, client_writer: asyncio.StreamWriter) -> None:
    global counter
    counter += 1
    cid = counter
    try:
        server_reader, server_writer = await asyncio.open_connection("127.0.0.1", TARGET)
    except Exception as exc:
        await log("proxy-error", cid, repr(exc).encode())
        client_writer.close()
        return
    await asyncio.gather(
        pump(client_reader, server_writer, "C->S", cid),
        pump(server_reader, client_writer, "S->C", cid),
    )


async def main() -> None:
    server = await asyncio.start_server(handle, "127.0.0.1", LISTEN)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
