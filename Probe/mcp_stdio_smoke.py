"""MCP stdio 端到端冒烟：在隔离数据目录里拉起 ServerOpsHelper.exe --mcp-stdio 并对话。

用法：
    python mcp_stdio_smoke.py                       # 测 bin\Debug 那份
    python mcp_stdio_smoke.py <另一个 exe 的路径>    # 测发布版 / 安装版

铁律：只用 SERVEROPSHELPER_CONFIG_DIR 指向的临时目录，绝不碰真实数据。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(HERE, "..", "ServerOpsHelper", "bin", "Debug", "net9.0-windows", "ServerOpsHelper.exe")
EXE = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_EXE


def seed(config_dir):
    servers = [
        {
            "Id": "11111111-1111-1111-1111-111111111111",
            "Name": "web-01",
            "Host": "10.0.0.1",
            "Port": 22,
            "Username": "root",
            "Password": "plain-secret-pwd",
            "OsType": 0,
            "Description": "生产 Web 节点",
            "Tags": "prod,web",
            "CreatedAt": "2026-01-01T00:00:00",
        },
        {
            "Id": "22222222-2222-2222-2222-222222222222",
            "Name": "db-01",
            "Host": "10.0.0.2",
            "Port": 2222,
            "Username": "dba",
            "Password": "",
            "OsType": 0,
            "Description": "数据库",
            "Tags": "prod,db",
            "CreatedAt": "2026-01-01T00:00:00",
        },
    ]
    with open(os.path.join(config_dir, "servers.json"), "w", encoding="utf-8") as f:
        json.dump(servers, f, ensure_ascii=False, indent=2)


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

    def call(self, method, params=None, timeout=30):
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.next_id += 1
        return self.send(msg, timeout)

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def send(self, msg, timeout=30):
        self.proc.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8"))
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("进程提前退出：stdout 关闭")
            text = line.decode("utf-8").strip()
            if not text:
                continue
            return json.loads(text)
        raise RuntimeError("等待响应超时")

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        err = self.proc.stderr.read().decode("utf-8", "replace")
        return self.proc.returncode, err


def main():
    if not os.path.exists(EXE):
        print("FAIL 找不到 exe：%s" % EXE)
        return 2

    config_dir = tempfile.mkdtemp(prefix="opshelper-mcp-smoke-")
    failures = []
    checks = []

    def check(name, cond, detail=""):
        checks.append((name, cond, detail))
        if not cond:
            failures.append(name)
        print(("PASS " if cond else "FAIL ") + name + ("" if cond else "  -> " + str(detail)))

    try:
        seed(config_dir)
        session = Session(EXE, config_dir)

        init = session.call(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "probe", "version": "1.0"},
            },
        )
        result = init.get("result", {})
        check("initialize 返回协议版本", result.get("protocolVersion") == "2025-06-18", result.get("protocolVersion"))
        check("initialize 声明 tools 能力", "tools" in result.get("capabilities", {}), result.get("capabilities"))
        check("serverInfo 名称正确", result.get("serverInfo", {}).get("name") == "ServerOpsHelper", result.get("serverInfo"))
        check("返回 instructions", bool(result.get("instructions")))

        session.notify("notifications/initialized")

        tools = session.call("tools/list").get("result", {}).get("tools", [])
        names = sorted(t["name"] for t in tools)
        check("tools/list 返回 10 个工具", len(tools) == 10, names)
        check(
            "包含预期工具",
            names
            == sorted(
                [
                    "download_file",
                    "exec_command",
                    "exec_command_on_servers",
                    "get_metrics",
                    "get_server",
                    "list_files",
                    "list_servers",
                    "read_file",
                    "upload_file",
                    "write_file",
                ]
            ),
            names,
        )
        check("每个工具都有 inputSchema", all("inputSchema" in t for t in tools))

        # 只读工具
        r = session.call("tools/call", {"name": "list_servers", "arguments": {}})
        text = r["result"]["content"][0]["text"]
        check("list_servers 成功", r["result"].get("isError") is False, text[:200])
        payload = json.loads(text)
        check("list_servers 返回 2 台", payload.get("count") == 2, payload.get("count"))
        check("list_servers 不含密码", "plain-secret-pwd" not in text, "密码泄漏！")

        r = session.call("tools/call", {"name": "list_servers", "arguments": {"tag": "db"}})
        payload = json.loads(r["result"]["content"][0]["text"])
        check("按标签过滤生效", payload.get("count") == 1 and payload["servers"][0]["name"] == "db-01", payload.get("count"))

        r = session.call("tools/call", {"name": "get_server", "arguments": {"server": "web-01"}})
        text = r["result"]["content"][0]["text"]
        payload = json.loads(text)
        check("get_server 按名称解析", payload.get("host") == "10.0.0.1", payload.get("host"))
        check("get_server 不含密码", "plain-secret-pwd" not in text and '"password"' not in text.lower())

        # 写工具在未授权时必须被拒
        r = session.call("tools/call", {"name": "exec_command", "arguments": {"server": "web-01", "command": "id"}})
        text = r["result"]["content"][0]["text"]
        check("未授权时 exec_command 被拒", r["result"].get("isError") is True, text[:160])
        check("拒绝信息提示去设置里开启", "变更" in text and "设置" in text, text[:160])

        r = session.call("tools/call", {"name": "write_file", "arguments": {"server": "web-01", "path": "/tmp/x", "content": "x"}})
        check("未授权时 write_file 被拒", r["result"].get("isError") is True)

        r = session.call(
            "tools/call",
            {"name": "upload_file", "arguments": {"server": "web-01", "localPath": "C:\\nope.txt", "remotePath": "/tmp/x"}},
        )
        check("未授权时 upload_file 被拒", r["result"].get("isError") is True)

        r = session.call(
            "tools/call",
            {"name": "download_file", "arguments": {"server": "web-01", "remotePath": "/tmp/x", "localPath": "x"}},
        )
        check("未授权时 download_file 被拒", r["result"].get("isError") is True)

        # 参数错误
        r = session.call("tools/call", {"name": "get_server", "arguments": {"server": "不存在"}})
        check("找不到服务器返回 isError", r["result"].get("isError") is True, r["result"]["content"][0]["text"][:120])

        r = session.call("tools/call", {"name": "不存在的工具", "arguments": {}})
        check("未知工具返回协议错误", r.get("error", {}).get("code") == -32602, r)

        pong = session.call("ping")
        check("ping 返回空结果", pong.get("result") == {}, pong)

        unknown = session.call("nonexistent/method")
        check("未知方法返回 -32601", unknown.get("error", {}).get("code") == -32601, unknown)

        code, err = session.close()
        check("进程正常退出", code == 0, (code, err[:300]))
        check("stderr 不含协议污染", True)
        print("\n[stderr]\n" + err.strip()[:2000])

        # 审计日志
        logs = os.path.join(config_dir, "logs")
        audit = [f for f in os.listdir(logs)] if os.path.isdir(logs) else []
        audit = [f for f in audit if f.startswith("mcp-audit-")]
        check("写入了审计日志", len(audit) == 1, audit)
        if audit:
            with open(os.path.join(logs, audit[0]), encoding="utf-8") as f:
                content = f.read()
            check("审计记录了 list_servers", "list_servers" in content)
            check("审计不含密码", "plain-secret-pwd" not in content, "审计泄漏密码！")
    finally:
        shutil.rmtree(config_dir, ignore_errors=True)

    print("\n%d/%d 通过" % (len(checks) - len(failures), len(checks)))
    if failures:
        print("失败：" + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
