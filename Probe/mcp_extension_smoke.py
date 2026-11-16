"""扩展包（清单式插件）端到端冒烟：真起 --mcp-stdio 进程，验证扩展工具在协议层的行为。

用法：
    python mcp_extension_smoke.py                       # 测 bin\\Debug 那份
    python mcp_extension_smoke.py <另一个 exe 的路径>    # 测发布版 / 安装版

铁律：只用 SERVEROPSHELPER_CONFIG_DIR 指向的临时目录，绝不碰真实数据。
服务器 host 故意用 127.0.0.1:1（连接立刻被拒），这样既能验到"闸门放行后真的去连了"，
又不需要任何真实机器、不会等 15 秒超时。
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

EXTENSION = {
    "id": "smoke-ext",
    "name": "冒烟扩展",
    "version": "1.0.0",
    "description": "probe only",
    "commands": [
        {
            "id": "echo",
            "name": "回显",
            "description": "只读命令",
            "command": "echo ${text}",
            "writeAccess": False,
            "parameters": [{"name": "text", "type": "string", "default": "hello"}],
        },
        {
            "id": "big",
            "name": "写入",
            "description": "变更命令",
            "command": "rm -f ${path}",
            "writeAccess": True,
            "parameters": [{"name": "path", "type": "path", "default": "/tmp/x"}],
        },
        {
            "id": "count",
            "name": "计数",
            "command": "tail -n ${n} /var/log/syslog",
            "writeAccess": False,
            "parameters": [{"name": "n", "type": "int", "default": "10", "min": 1, "max": 100}],
        },
    ],
}

SERVERS = [
    {
        "Id": "11111111-1111-1111-1111-111111111111",
        "Name": "dead-01",
        "Host": "127.0.0.1",
        "Port": 1,
        "Username": "root",
        "Password": "plain-secret-pwd",
        "OsType": 0,
        "Description": "连接必然被拒",
        "Tags": "probe",
        "CreatedAt": "2026-01-01T00:00:00",
    }
]


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

    def call(self, method, params=None, timeout=40):
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.next_id += 1
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

    def notify(self, method, params=None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        return self.proc.returncode, self.proc.stderr.read().decode("utf-8", "replace")


def seed(config_dir):
    with open(os.path.join(config_dir, "servers.json"), "w", encoding="utf-8") as f:
        json.dump(SERVERS, f, ensure_ascii=False, indent=2)

    ext_dir = os.path.join(config_dir, "extensions", "smoke")
    os.makedirs(ext_dir, exist_ok=True)
    with open(os.path.join(ext_dir, "extension.json"), "w", encoding="utf-8") as f:
        json.dump(EXTENSION, f, ensure_ascii=False, indent=2)


def main():
    if not os.path.exists(EXE):
        print("FAIL 找不到 exe：%s" % EXE)
        return 2

    config_dir = tempfile.mkdtemp(prefix="opshelper-ext-smoke-")
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

        session.call(
            "initialize",
            {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "probe", "version": "1.0"},
            },
        )
        session.notify("notifications/initialized")

        tools = session.call("tools/list").get("result", {}).get("tools", [])
        names = sorted(t["name"] for t in tools)

        check("扩展工具出现在 tools/list（10 内置 + 3 扩展）", len(tools) == 13, names)
        check("扩展工具命名 ext_<包>_<命令>", "ext_smoke-ext_echo" in names, names)
        check("内置工具仍然齐全", "list_servers" in names and "exec_command" in names, names)

        echo_tool = next((t for t in tools if t["name"] == "ext_smoke-ext_echo"), None)
        props = (echo_tool or {}).get("inputSchema", {}).get("properties", {})
        check("扩展工具 schema 含 server 与参数", "server" in props and "text" in props, props)
        check("只读扩展工具标注 readOnlyHint",
              (echo_tool or {}).get("annotations", {}).get("readOnlyHint") is True,
              (echo_tool or {}).get("annotations"))

        big_tool = next((t for t in tools if t["name"] == "ext_smoke-ext_big"), None)
        check("变更扩展工具不标注 readOnlyHint",
              (big_tool or {}).get("annotations", {}).get("readOnlyHint") is False,
              (big_tool or {}).get("annotations"))

        # 变更类扩展命令：未授权必须被闸门拦住（不允许走到 SSH）
        r = session.call("tools/call", {"name": "ext_smoke-ext_big", "arguments": {"server": "dead-01"}})
        text = r["result"]["content"][0]["text"]
        check("未授权时变更扩展命令被拒", r["result"].get("isError") is True, text[:160])
        check("拒绝信息指向设置页", "设置" in text, text[:160])

        # 参数校验发生在连 SSH 之前：这里既验规则，也证明"没白连一次"
        r = session.call("tools/call", {"name": "ext_smoke-ext_count", "arguments": {"server": "dead-01", "n": 999}})
        text = r["result"]["content"][0]["text"]
        check("int 参数越界被拒且不连服务器", r["result"].get("isError") is True and "不能大于" in text, text[:160])

        # 只读扩展命令在未授权下应当放行到"真的去连"这一步（连接被拒，而不是权限被拒）
        r = session.call("tools/call", {"name": "ext_smoke-ext_echo", "arguments": {"server": "dead-01", "text": "hi"}})
        text = r["result"]["content"][0]["text"]
        check("只读扩展命令不受写权限限制", "已被禁用" not in text, text[:200])
        check("只读扩展命令确实尝试了 SSH", r["result"].get("isError") is True and len(text) > 0, text[:200])

        r = session.call("tools/call", {"name": "ext_not-there_x", "arguments": {"server": "dead-01"}})
        check("未知扩展工具返回协议错误", r.get("error", {}).get("code") == -32602, r)

        code, err = session.close()
        check("进程正常退出", code == 0, (code, err[:300]))

        logs = os.path.join(config_dir, "logs")
        audit = sorted(f for f in os.listdir(logs)) if os.path.isdir(logs) else []
        audit = [f for f in audit if f.startswith("mcp-audit-")]
        check("写入了审计日志", len(audit) == 1, audit)
        if audit:
            with open(os.path.join(logs, audit[0]), encoding="utf-8") as f:
                content = f.read()
            check("审计记录了扩展工具", "ext_smoke-ext_" in content, content[:300])
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
