# -*- coding: utf-8 -*-
"""把 Inno Setup 以「便携模式」装进工作区 .tools 目录（不动系统、不写注册表）。

⚠️ 必须用 Python 启动安装程序：Git Bash 会把参数里的反斜杠转成正斜杠，
   Inno Setup 会报「无法访问网络位置」—— 2026-10-17 在 WiX 那边踩过。
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(HERE, "InnoSetup6")
SETUP = os.path.join(HERE, "innosetup-6.7.3.exe")
LOG = os.path.join(HERE, "is_install.log")

args = [
    SETUP,
    "/PORTABLE=1",
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/NOICONS",
    "/DIR=" + TARGET,
    "/LOG=" + LOG,
]
print("run:", args)
p = subprocess.run(args)
print("exit:", p.returncode)

iscc = os.path.join(TARGET, "ISCC.exe")
print("ISCC exists:", os.path.exists(iscc))
if os.path.exists(iscc):
    print("size:", os.path.getsize(iscc))
lang = os.path.join(TARGET, "Languages", "ChineseSimplified.isl")
print("ChineseSimplified.isl:", os.path.exists(lang))
print("Languages:", sorted(os.listdir(os.path.join(TARGET, "Languages")))[:12] if os.path.isdir(os.path.join(TARGET, "Languages")) else "N/A")
sys.exit(0)
