#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓取微软官方 Windows 直链，更新 CTPrep 的链接清单。

链接抓取复用了内置的第三方工具（tools/microsoft-iso-directlink/，MIT 协议，
复刻 Fido 流程）；本脚本只负责调用它、把结果合并进 images.json。

微软官方直链约 24 小时后失效，建议每天执行一次（服务器 cron / Windows
任务计划 / GitHub Actions 都可以）。只维护两个家族键（"Windows 11" /
"Windows 10"，家族前缀匹配自动覆盖各自所有子版本，不用随版本号改）；
自建分发的条目（Win8.1 / Win7、PE 等）一律原样保留。

用法：
    python tools/sync-microsoft-links.py                 # 更新 docs/images.json
    python tools/sync-microsoft-links.py --print         # 只打印，不改文件
    python tools/sync-microsoft-links.py --only "Windows 10" --print   # 调试单个目标

依赖：Python 3.8+；bash + curl（Windows 需 Git for Windows，Linux 自带）。
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
VENDORED = os.path.join(HERE, "microsoft-iso-directlink", "get_microsoft_iso_link.sh")

# 由本脚本维护的家族键 -> 微软 EditionId 候选（逗号分隔，按序尝试，命中即用）。
# EditionId 会随微软换版变化；核查方式：
#   bash tools/microsoft-iso-directlink/get_microsoft_iso_link.sh list
# 或翻 pbatard/Fido 源码的 $WindowsVersions 表（里面的 Id 字段）。
TARGETS = [
    ("Windows 11", "3813,3321,3324"),
    ("Windows 10", "2618"),
]

RUN_TIMEOUT = 600  # 秒；工具内部最多重试 3 次，留足余量

ITEM_RE = re.compile(r"^\[(\d+)\]\s+(.*?)(?:\s{2,}[\d.]+\s*GB)?\s*$")
URL_RE = re.compile(r"^\s+(https?://\S+)\s*$")


def find_bash():
    for name in ("bash", "bash.exe"):
        found = shutil.which(name)
        if found:
            return found
    program_files = os.environ.get("ProgramFiles", r"C:\Program Files")
    for candidate in (os.path.join(program_files, "Git", "bin", "bash.exe"),
                      os.path.join(program_files, "Git", "usr", "bin", "bash.exe")):
        if os.path.isfile(candidate):
            return candidate
    return None


def parse_links(stdout):
    """从工具输出里解析 [(文件名, 直链)] 列表。"""
    pairs = []
    pending = None
    for line in stdout.splitlines():
        item = ITEM_RE.match(line)
        if item:
            pending = item.group(2).strip()
            continue
        url = URL_RE.match(line)
        if url and pending is not None:
            pairs.append((pending, url.group(1)))
            pending = None
    return pairs


def pick_x64(pairs):
    for name, url in pairs:
        if "x64" in name.lower():
            return url
    return pairs[0][1] if pairs else None


def fetch_target(bash, key, edition_ids, lang):
    """调用内置工具拿一个目标的 x64 直链；失败抛 RuntimeError。"""
    with tempfile.TemporaryDirectory(prefix="ctprep-mslink-") as workdir:
        try:
            proc = subprocess.run(
                [bash, VENDORED, edition_ids, lang],
                cwd=workdir,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True, encoding="utf-8", errors="replace",
                timeout=RUN_TIMEOUT)
        except subprocess.TimeoutExpired:
            raise RuntimeError("%s：工具运行超时（%d 秒）" % (key, RUN_TIMEOUT))

    for line in (proc.stdout or "").splitlines():
        print("      " + line)
    for line in (proc.stderr or "").splitlines():
        print("      " + line)
    if proc.returncode != 0:
        raise RuntimeError("%s：工具返回码 %d" % (key, proc.returncode))

    pairs = parse_links(proc.stdout or "")
    if not pairs:
        raise RuntimeError("%s：工具输出里没有解析到任何直链" % key)
    return pick_x64(pairs)


def load_manifest(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except json.JSONDecodeError as exc:
        raise SystemExit("清单不是合法 JSON（%s）：%s" % (path, exc))


def main():
    parser = argparse.ArgumentParser(description="抓取微软官方直链并合并进 CTPrep 链接清单")
    parser.add_argument("--out", default="docs/images.json", help="要更新的清单文件（默认 docs/images.json）")
    parser.add_argument("--lang", default="zh-cn", help="语言（模糊匹配，如 zh-cn / 简体 / en-us，默认 zh-cn）")
    parser.add_argument("--print", dest="print_only", action="store_true", help="只打印抓到的链接，不改文件")
    parser.add_argument("--only", default=None, help='只处理指定键（调试用，如 "Windows 10"）')
    args = parser.parse_args()

    if not os.path.isfile(VENDORED):
        print("ERROR: 未找到内置工具：%s" % VENDORED)
        return 1
    bash = find_bash()
    if not bash:
        print("ERROR: 未找到 bash。Windows 请安装 Git for Windows（Git Bash）；Linux 自带。")
        return 1

    targets = TARGETS
    if args.only:
        targets = [t for t in TARGETS if t[0].lower() == args.only.lower()]
        if not targets:
            print("ERROR: --only 只支持：%s" % "、".join(t[0] for t in TARGETS))
            return 1

    results = {}
    for key, edition_ids in targets:
        print("==> 正在获取微软官方链接：%s（EditionId=%s，语言 %s）" % (key, edition_ids, args.lang))
        try:
            url = fetch_target(bash, key, edition_ids, args.lang)
        except RuntimeError as exc:
            print("    [失败] %s" % exc)
            print("    提示：被微软短期限流（SentinelReject）时等 10-30 分钟再跑，不要连续重试。")
            return 2
        print("    选取直链：%s" % url)
        results[key] = url

    if args.print_only:
        print()
        print(json.dumps({key: {"url": url} for key, url in results.items()},
                         ensure_ascii=False, indent=2))
        return 0

    manifest = load_manifest(args.out)
    existing = manifest.get("images") or {}
    merged = {}
    # 自动键放最前：程序的家族前缀匹配取「文件序第一个」命中的键，
    # 这样非精确命中的机器优先拿到官方最新版；自建条目原样排在后面。
    for key, url in results.items():
        merged[key] = {"url": url, "sha256": ""}
        print("    已写入清单：%s" % key)
    for key, value in existing.items():
        if key not in merged:
            merged[key] = value
    manifest["images"] = merged
    manifest["schema"] = int(manifest.get("schema") or 1)
    manifest["updated"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print("清单已更新：%s" % args.out)
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
