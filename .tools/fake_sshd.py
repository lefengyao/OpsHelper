#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
假的 SSH 服务端 —— 只用来验证 ServerOpsHelper 的 SSH 连接池行为。

为什么需要它：连接复用/握手重试是"连接层"的逻辑，光看代码或编译通过都证明不了。
这里用一个真实的 SSH 协议栈（paramiko）在本机起服务，能精确控制"第几次 TCP 连接被直接关掉"
（复现 sshd MaxStartups 拒绝握手的现象），并统计客户端到底握了几次手。

用法：
    python fake_sshd.py --port 2222 [--reject-first 2] [--log fake_sshd.log]

支持的 exec：
    echo <text>   回显
    sleep <sec>   睡 N 秒（测超时）
    exit <code>   指定退出码
    其它           返回 "unknown command"

账号固定 admin / probe-pass（与探针里的 ServerInfo 对应）。
"""
import argparse
import socket
import sys
import threading
import time

import paramiko


class FakeServer(paramiko.ServerInterface):
    def __init__(self, log):
        self.log = log

    def check_auth_password(self, username, password):
        if username == "admin" and password == "probe-pass":
            return paramiko.AUTH_SUCCESSFUL
        self.log("auth failed for %s" % username)
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, chanid):
        if kind == "session":
            return paramiko.OPEN_SUCCEEDED
        return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_exec_request(self, channel, command):
        text = command.decode("utf-8", "replace")
        self.log("exec: %s" % text)
        threading.Thread(target=self._run, args=(channel, text), daemon=True).start()
        return True

    def _run(self, channel, command):
        code = 0
        try:
            if command.startswith("echo "):
                channel.sendall((command[5:] + "\n").encode("utf-8"))
            elif command.startswith("sleep "):
                time.sleep(float(command[6:]))
            elif command.startswith("exit "):
                code = int(command[5:])
            elif command == "pwd":
                channel.sendall(b"/home/admin\n")
            else:
                channel.sendall(b"unknown command\n")
        except Exception as exc:  # 客户端提前断开时这里会炸，属于正常现象
            self.log("handler error: %r" % (exc,))
        finally:
            try:
                channel.send_exit_status(code)
            except Exception:
                pass
            try:
                channel.close()
            except Exception:
                pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=2222)
    parser.add_argument("--reject-first", type=int, default=0,
                        help="前 N 次 TCP 连接立刻关闭，模拟 sshd MaxStartups 拒绝")
    parser.add_argument("--log", default="fake_sshd.log")
    args = parser.parse_args()

    lock = threading.Lock()

    def log(text):
        line = "%s %s" % (time.strftime("%H:%M:%S"), text)
        with lock:
            print(line, flush=True)
            with open(args.log, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")

    host_key = paramiko.RSAKey.generate(2048)
    log("listening on 127.0.0.1:%d reject_first=%d" % (args.port, args.reject_first))

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", args.port))
    listener.listen(32)

    accepted = 0
    while True:
        client, _addr = listener.accept()
        accepted += 1
        log("accept #%d" % accepted)

        if accepted <= args.reject_first:
            # 立刻关掉、一个字节都不发 —— 客户端读 banner 时会拿到空响应，
            # 正是 "does not contain an SSH identification string" 的成因。
            try:
                client.close()
            except Exception:
                pass
            log("reject #%d (simulated MaxStartups)" % accepted)
            continue

        threading.Thread(target=serve, args=(client, host_key, log), daemon=True).start()


def serve(client, host_key, log):
    try:
        transport = paramiko.Transport(client)
        transport.add_server_key(host_key)
        transport.start_server(server=FakeServer(log))
        while transport.is_active():
            time.sleep(0.1)
    except Exception as exc:
        log("session ended: %r" % (exc,))
    finally:
        try:
            client.close()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
