"""验证 get_metrics 的差值类指标（CPU 使用率 / 网络吞吐）与 lastConnectedAt 写回。

背景（2026-10-18 老板真机发现）：
  1. 三台机器 CPU 使用率与网络吞吐恒为 null；
  2. lastConnectedAt 连上了也还是 null。
根因：
  1. McpToolExecutor.GetMetricsAsync 取的是第一次采样（没有差值的那个），
     而不是 tcs 里第二次采样的结果；
  2. ServerInfo.LastConnectedAt 全项目只读不写，从来没被赋值。

用法：
    python mcp_metrics_check.py <exe 路径> [真实 servers.json 路径]

铁律：**只把 servers.json 复制**到临时目录，真实数据目录一个字节都不改。
      复制的密码是 DPAPI 密文，同一用户下可正常解密。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    HERE, "..", "ServerOpsHelper", "publish", "win-x64", "ServerOpsHelper.exe")
REAL_SERVERS = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "ServerOpsHelper", "servers.json")

passed = 0
failed = 0


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  PASS  {name}" + (f"  ({detail})" if detail else ""))
    else:
        failed += 1
        print(f"  FAIL  {name}" + (f"  ({detail})" if detail else ""))


class Session:
    def __init__(self, exe, config_dir):
        env = dict(os.environ)
        env["SERVEROPSHELPER_CONFIG_DIR"] = config_dir
        self.proc = subprocess.Popen(
            [exe, "--mcp-stdio"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
        )
        self.next_id = 1

    def call(self, method, params=None, timeout=60):
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.next_id += 1
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

        # 逐行读，跳过任何诊断输出，只认有 id 的那条响应
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("stdio 提前关闭：" +
                                   self.proc.stderr.read().decode("utf-8", "replace")[:400])
            text = line.decode("utf-8", "replace").strip()
            if not text:
                continue
            try:
                data = json.loads(text)
            except json.JSONDecodeError:
                continue
            if data.get("id") == self.next_id - 1:
                return data

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=8)
        except Exception:
            self.proc.kill()


def main():
    if not os.path.isfile(EXE):
        print(f"找不到 exe：{EXE}")
        return 2
    if not os.path.isfile(REAL_SERVERS):
        print(f"找不到 servers.json：{REAL_SERVERS}")
        return 2

    tmp = tempfile.mkdtemp(prefix="opshelper-metrics-")
    try:
        # 只复制这一个文件，绝不整目录复制（那会把 WebView2 缓存等也拖进来）
        seed = os.path.join(tmp, "servers.json")
        shutil.copy2(REAL_SERVERS, seed)

        with open(seed, encoding="utf-8") as fh:
            servers = json.load(fh)
        names = [s.get("Name") for s in servers]
        print(f"隔离目录：{tmp}")
        print(f"待测服务器：{names}\n")

        s = Session(EXE, tmp)
        try:
            s.call("initialize", {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "metrics-check", "version": "1.0"},
            })
            s.call("notifications/initialized")

            cpu_values = {}
            net_values = {}

            for name in names:
                print(f"[{name}] get_metrics")
                resp = s.call("tools/call", {"name": "get_metrics", "arguments": {"server": name}})
                result = resp.get("result") or {}
                if result.get("isError"):
                    check(f"{name} 调用成功", False, str(result.get("content"))[:200])
                    continue

                # MCP 的 content[0].text 是 JSON 字符串
                payload = json.loads(result["content"][0]["text"])
                cpu = (payload.get("cpu") or {}).get("usagePercent")
                net = payload.get("network") or {}
                rx = net.get("rxBytesPerSec")
                tx = net.get("txBytesPerSec")

                cpu_values[name] = cpu
                net_values[name] = (rx, tx)

                print(f"        cores={payload['cpu']['cores']} cpu={cpu} "
                      f"iface={net.get('interface')} rx={rx} tx={tx}")

                check(f"{name} cpu.usagePercent 有值", cpu is not None, f"{cpu}")
                check(f"{name} network.rxBytesPerSec 有值", rx is not None, f"{rx}")
                check(f"{name} network.txBytesPerSec 有值", tx is not None, f"{tx}")
                check(f"{name} 网卡名不是 lo",
                      (net.get("interface") or "") not in ("", "lo"),
                      str(net.get("interface")))

            # ── lastConnectedAt 写回 ──
            print("\n[lastConnectedAt] 重新读服务器列表")
            resp = s.call("tools/call", {"name": "list_servers", "arguments": {}})
            result = resp.get("result") or {}
            listed = json.loads(result["content"][0]["text"])
            # list_servers 返回 {count, servers:[...]} 或直接数组，两种都兼容
            items = listed.get("servers") if isinstance(listed, dict) else listed

            for item in items or []:
                name = item.get("name")
                ts = item.get("lastConnectedAt")
                check(f"{name} lastConnectedAt 有值", ts is not None, str(ts))

            # 再确认真的落盘了（不是只在内存里）
            with open(seed, encoding="utf-8") as fh:
                on_disk = json.load(fh)
            persisted = [s.get("Name") for s in on_disk if s.get("LastConnectedAt")]
            check("lastConnectedAt 已持久化到 servers.json",
                  len(persisted) == len(names),
                  f"{len(persisted)}/{len(names)} 台")
        finally:
            s.close()
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print(f"\n===== {passed} 通过 / {failed} 失败 =====")
    if failed == 0 and cpu_values and all(v is not None for v in cpu_values.values()) \
            and all(rx is not None for rx, _ in net_values.values()):
        print("结论：CPU 使用率与网络吞吐均能取到，lastConnectedAt 正常写回。")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
