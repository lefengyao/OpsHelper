"""安装程序端到端回归测试：4 个场景全自动跑完并给出结论。

场景：
  A. 自选目录 装 -> 卸（验证"能选目录" + "卸干净"）
  B. 默认路径 装 -> 卸（验证不带参数时也能正常）
  C. 重装（验证升级/覆盖安装）
  D. 数据目录不受影响（验证"卸载不删用户数据"这条铁律）

全程静默、参数化路径，绝不碰真实数据目录。每个场景都断言：
退出码 == 0 且 目标目录文件数符合预期。

用法：python e2e_matrix.py <setup.exe>
"""
import os
import shutil
import subprocess
import sys

EXE = sys.argv[1]
BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "e2e_matrix")
LOCAL = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "ServerOpsHelper")

# 用户数据目录（铁律：安装/卸载都不许动它）
DATA = os.path.join(os.environ["LOCALAPPDATA"], "ServerOpsHelper")

results = []


def sh(args, log):
    p = subprocess.run([EXE] + args + ["-l", log], capture_output=True, text=True)
    return p.returncode


def count(d):
    if not os.path.isdir(d):
        return 0
    n = 0
    for _r, _dd, ff in os.walk(d):
        n += len(ff)
    return n


def case(name, ok, detail):
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name} —— {detail}")


print(f"安装程序: {EXE}\n")

# 数据目录指纹（测前）
before_data = count(DATA)
print(f"用户数据目录（测前）: {DATA}  {before_data} 个文件\n")

# ---------------- 场景 A：自选目录（选的是**父目录**） ----------------
# 2026-09-30 起的安装行为：用户选 E:\AI，程序装进 E:\AI\ServerOpsHelper\（自动建一层）。
# 所以这里传给安装程序的 target 是「父目录」，断言检查的是它下面的 ServerOpsHelper 子目录。
print("【场景 A】自选父目录 安装 -> 卸载（自动创建应用子文件夹）")
shutil.rmtree(BASE, ignore_errors=True)
os.makedirs(BASE, exist_ok=True)
parent = os.path.join(BASE, "myapp")            # 用户在向导里"选择的路径"
target = os.path.join(parent, "ServerOpsHelper")  # 程序实际落点

rc = sh(["/quiet", "/norestart", f"InstallFolder={parent}"], os.path.join(BASE, "a_i.log"))
n = count(target)
case("A1 自选父目录安装（自动建 ServerOpsHelper 子文件夹）",
     rc == 0 and n > 200, f"退出码={rc} 子目录文件数={n}")

# 「自动新建文件夹」的直接证据：父目录里除应用文件夹外不应有散落文件
# （安装若失败，父目录可能压根不存在 —— 这里要兜住，别让脚本崩掉掩盖真实原因）
if os.path.isdir(parent):
    stray = [x for x in os.listdir(parent) if x != "ServerOpsHelper"]
    case("A1b 父目录里没有散落的程序文件（都收进了子文件夹）",
         len(stray) == 0, f"父目录里多了 {stray[:5]}" if stray else "父目录只有 ServerOpsHelper 一个子文件夹")
else:
    case("A1b 父目录里没有散落的程序文件（都收进了子文件夹）",
         False, "父目录未被创建 —— 安装根本没成功（见 A1 的退出码）")

rc = sh(["/uninstall", "/quiet", "/norestart"], os.path.join(BASE, "a_u.log"))
left = count(target)
case("A2 自选父目录卸载", rc == 0 and left == 0, f"退出码={rc} 残留={left}")

# ---------------- 场景 B：默认路径 ----------------
print("\n【场景 B】默认路径 安装 -> 卸载")
rc = sh(["/quiet", "/norestart"], os.path.join(BASE, "b_i.log"))
n = count(LOCAL)
case("B1 默认路径安装", rc == 0 and n > 200, f"退出码={rc} 文件数={n}")

rc = sh(["/uninstall", "/quiet", "/norestart"], os.path.join(BASE, "b_u.log"))
left = count(LOCAL)
case("B2 默认路径卸载", rc == 0 and left == 0, f"退出码={rc} 残留={left}")

# ---------------- 场景 C：重装（覆盖安装） ----------------
print("\n【场景 C】重装（同版本覆盖）")
rc1 = sh(["/quiet", "/norestart"], os.path.join(BASE, "c_i1.log"))
rc2 = sh(["/quiet", "/norestart"], os.path.join(BASE, "c_i2.log"))
n = count(LOCAL)
case("C1 重复安装不报错", rc1 == 0 and rc2 == 0, f"首次={rc1} 再次={rc2} 文件数={n}")
sh(["/uninstall", "/quiet", "/norestart"], os.path.join(BASE, "c_u.log"))

# ---------------- 场景 D：数据目录不受影响 ----------------
print("\n【场景 D】用户数据目录是否被动过（铁律）")
after_data = count(DATA)
case("D1 数据目录零改动", before_data == after_data,
     f"测前={before_data} 测后={after_data}")

# ---------------- 汇总 ----------------
print("\n" + "=" * 62)
passed = sum(1 for _n, ok, _d in results if ok)
print(f"结果：{passed}/{len(results)} 通过")
for n, ok, d in results:
    if not ok:
        print(f"  未通过: {n} —— {d}")
print("=" * 62)

shutil.rmtree(BASE, ignore_errors=True)
sys.exit(0 if passed == len(results) else 1)
