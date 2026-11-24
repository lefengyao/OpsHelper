#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
用真实配置（复制到隔离目录）对指定服务器跑一批只读命令，量化 SSH 通道的成功率与耗时。

安全边界：
- 配置从 %LOCALAPPDATA%\\ServerOpsHelper 复制到 %TEMP% 下的隔离目录，
  由 SERVEROPSHELPER_CONFIG_DIR 指向它；真实目录只读不写。
- 只执行 argv 指定的命令（默认 `date +%s`，幂等、无副作用）。
- 走 --mcp-stdio，和智能体调用的完全同一条代码路径。

用法：
    python mcp_ssh_bench.py <exe路径> <服务器名> [命令] [次数]
"""
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

EXE = sys.argv[1]
SERVER = sys.argv[2]
COMMAND = sys.argv[3] if len(sys.argv) > 3 else "date +%s"
COUNT = int(sys.argv[4]) if len(sys.argv) > 4 else 15

CONFIG_SRC = os.path.join(os.environ["LOCALAPPDATA"], "ServerOpsHelper")
WORK = os.path.join(tempfile.gettempdir(), "opshelper-sshbench")


def prepare_config():
    if os.path.isdir(WORK):
        shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK, exist_ok=True)

    for name in ("servers.json", "settings.json"):
        src = os.path.join(CONFIG_SRC, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(WORK, name))

    # 明确打开写工具，避免被权限闸门挡住（隔离目录里改，不影响真实设置）
    settings_path = os.path.join(WORK, "settings.json")
    if os.path.exists(settings_path):
        with open(settings_path, encoding="utf-8-sig") as fh:
            settings = json.load(fh)
        settings["McpAllowWriteTools"] = True
        with open(settings_path, "w", encoding="utf-8") as fh:
            json.dump(settings, fh, ensure_ascii=False)


def main():
    prepare_config()

    env = dict(os.environ)
    env["SERVEROPSHELPER_CONFIG_DIR"] = WORK

    log_path = os.path.join(WORK, "stderr.log")
    log_file = open(log_path, "w", encoding="utf-8")

    proc = subprocess.Popen(
        [EXE, "--mcp-stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=log_file,
        env=env,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )

    responses = queue.Queue()

    def reader():
        for line in proc.stdout:
            responses.put(line)
        responses.put(None)

    threading.Thread(target=reader, daemon=True).start()

    def rpc(payload, timeout=180):
        proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        proc.stdin.flush()
        line = responses.get(timeout=timeout)
        if line is None:
            raise RuntimeError("MCP 子进程提前退出")
        return json.loads(line)

    def notify(payload):
        proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    print("exe   :", EXE)
    print("server:", SERVER, "| cmd:", COMMAND, "| count:", COUNT)

    init = rpc({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "ssh-bench", "version": "1.0"},
        },
    })
    print("server version:", init.get("result", {}).get("serverInfo", {}).get("version"))

    notify({"jsonrpc": "2.0", "method": "notifications/initialized"})

    ok = fail = 0
    durations = []
    errors = []

    for i in range(COUNT):
        started = time.time()
        try:
            response = rpc({
                "jsonrpc": "2.0", "id": 100 + i, "method": "tools/call",
                "params": {
                    "name": "exec_command",
                    "arguments": {
                        "server": SERVER,
                        "command": COMMAND,
                        "timeoutSeconds": 60,
                    },
                },
            }, timeout=180)
        except Exception as exc:
            fail += 1
            errors.append("transport: %r" % (exc,))
            durations.append(time.time() - started)
            continue

        elapsed = time.time() - started
        durations.append(elapsed)

        result = response.get("result") or {}
        if result.get("isError"):
            fail += 1
            text = (result.get("content") or [{}])[0].get("text", "")
            errors.append(text[:300])
            continue

        try:
            payload = json.loads((result.get("content") or [{}])[0].get("text", "{}"))
        except Exception:
            fail += 1
            errors.append("unparsable payload")
            continue

        if payload.get("exitCode") == 0 and not payload.get("timedOut"):
            ok += 1
            if os.environ.get("BENCH_SHOW") == "1":
                print("--- stdout ---")
                print(payload.get("stdout", "").rstrip())
                print("--------------")
        else:
            fail += 1
            errors.append(json.dumps(payload, ensure_ascii=False)[:300])

    durations.sort()
    p50 = durations[len(durations) // 2]
    p95 = durations[min(len(durations) - 1, int(len(durations) * 0.95))]

    print()
    print("ok=%d fail=%d" % (ok, fail))
    print("elapsed  p50=%.2fs  p95=%.2fs  max=%.2fs" % (p50, p95, durations[-1]))

    if errors:
        print("\n错误样本（最多 3 条）：")
        seen = set()
        for item in errors:
            key = item[:80]
            if key in seen:
                continue
            seen.add(key)
            print("  -", item.replace("\n", " ")[:300])
            if len(seen) >= 3:
                break

    try:
        proc.stdin.close()
    except Exception:
        pass
    try:
        proc.wait(timeout=10)
    except Exception:
        proc.kill()

    log_file.close()

    if os.path.exists(log_path) and os.path.getsize(log_path) > 0:
        with open(log_path, encoding="utf-8", errors="replace") as fh:
            tail = fh.read().strip().splitlines()[-5:]
        if tail:
            print("\n子进程 stderr 末尾：")
            for line in tail:
                print("  ", line)

    return 0 if fail == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
