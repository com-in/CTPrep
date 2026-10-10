#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓取微软官方 Windows / Windows Server 直链，更新 CTPrep 的链接清单。

链接抓取分两条路径：
  1) Fido 家族（Windows 11 / Windows 10）走内置第三方工具
     （tools/microsoft-iso-directlink/，MIT 协议，复刻 Fido 流程）；
  2) fwlink 家族（Windows Server 2025/2022/2019/2016/2012 R2）直接跟随
     微软 go.microsoft.com 短链的 302，拿到 download.prss.microsoft.com
     上的最终 ISO 直链。

本脚本只负责调用/抓取、把结果合并进 images.json。微软官方直链约 24 小时
后失效，建议每天执行一次（服务器 cron / Windows 任务计划 / GitHub Actions
都可以）。自建分发的条目（Win8.1 / Win7、PE 等）一律原样保留。

用法：
    python tools/sync-microsoft-links.py                 # 更新 docs/images.json
    python tools/sync-microsoft-links.py --print         # 只打印，不改文件
    python tools/sync-microsoft-links.py --only "Windows 10" --print
    python tools/sync-microsoft-links.py --only "Windows Server 2025" --print

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

# 由本脚本维护的 Fido 家族键 -> 微软 EditionId 候选（逗号分隔，按序尝试，命中即用）。
# EditionId 会随微软换版变化；核查方式：
#   bash tools/microsoft-iso-directlink/get_microsoft_iso_link.sh list
# 或翻 pbatard/Fido 源码的 $WindowsVersions 表（里面的 Id 字段）。
TARGETS = [
    ("Windows 11", "3813,3321,3324"),
    ("Windows 10", "2618"),
]

# 由本脚本维护的 fwlink 家族键 -> go.microsoft.com 短链。
# 这些短链每跳都是 302，最终落到 download.prss.microsoft.com 上的 ISO 直链，
# 约 24 小时后失效，与 Fido 家族同样需要每日刷新。
FW_LINK_TARGETS = [
    ("Windows Server 2025",
     "https://go.microsoft.com/fwlink/?linkid=2345732&clcid=0x804&culture=zh-cn&country=cn"),
    ("Windows Server 2022",
     "https://go.microsoft.com/fwlink/p/?LinkID=2195280&clcid=0x804&culture=zh-cn&country=CN"),
    ("Windows Server 2019",
     "https://go.microsoft.com/fwlink/p/?LinkID=2195167&clcid=0x804&culture=zh-cn&country=CN"),
    ("Windows Server 2016",
     "https://go.microsoft.com/fwlink/p/?LinkID=2195174&clcid=0x804&culture=zh-cn&country=CN"),
    ("Windows Server 2012 R2",
     "https://go.microsoft.com/fwlink/p/?LinkID=2195443&clcid=0x804&culture=zh-cn&country=CN"),
]

RUN_TIMEOUT = 600   # 秒；Fido 工具内部最多重试 3 次，留足余量
CURL_TIMEOUT = 30   # 秒；fwlink 单次跟随重定向的超时（每跳共享）

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
    """从 Fido 工具输出里解析 [(文件名, 直链)] 列表。"""
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
    """走内置 Fido 工具拿一个目标的 x64 直链；失败抛 RuntimeError。"""
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


def fetch_fwlink(bash, key, url):
    """跟随 go.microsoft.com 短链的 302，返回最终 ISO 直链。

    用 curl 的 -I（HEAD）+ -L（跟随重定向）+ -o /dev/null（丢弃正文），
    再加 -w '%{url_effective}' 输出最终 URL。HEAD 请求不会真的下载 ISO，
    所以即使最终地址是几 GB 的文件，也只消耗一个响应头的时间。

    URL 通过 argv 传给 bash，不经过 shell 展开，因此 URL 里的 & 不会被
    当成后台运行符。
    """
    script = 'curl -sIL --max-time %d -o /dev/null -w "%%{url_effective}\\n" "$1"' % CURL_TIMEOUT
    try:
        proc = subprocess.run(
            [bash, "-c", script, "bash", url],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            timeout=CURL_TIMEOUT + 10)
    except subprocess.TimeoutExpired:
        raise RuntimeError("%s：fwlink 解析超时（%d 秒）" % (key, CURL_TIMEOUT))

    if proc.returncode != 0:
        raise RuntimeError("%s：curl 返回码 %d（%s）"
                           % (key, proc.returncode, (proc.stderr or "").strip()))

    lines = [ln.strip() for ln in (proc.stdout or "").splitlines() if ln.strip()]
    final = lines[-1] if lines else ""
    if not final.lower().startswith("http"):
        raise RuntimeError("%s：未解析到最终 URL（curl 输出：%r）" % (key, final))
    return final


def load_manifest(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except json.JSONDecodeError as exc:
        raise SystemExit("清单不是合法 JSON（%s）：%s" % (path, exc))


def manifest_age_hours(path):
    """清单 updated 字段距今的小时数；缺失或无法解析时返回 None。"""
    stamp = str(load_manifest(path).get("updated") or "").strip()
    if not stamp:
        return None
    try:
        moment = datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        return None
    return (datetime.now(timezone.utc) - moment).total_seconds() / 3600.0


def main():
    parser = argparse.ArgumentParser(description="抓取微软官方直链并合并进 CTPrep 链接清单")
    parser.add_argument("--out", default="docs/images.json", help="要更新的清单文件（默认 docs/images.json）")
    parser.add_argument("--lang", default="zh-cn", help="Fido 语言（模糊匹配，如 zh-cn / 简体 / en-us，默认 zh-cn）")
    parser.add_argument("--print", dest="print_only", action="store_true", help="只打印抓到的链接，不改文件")
    parser.add_argument("--only", default=None,
                        help='只处理指定键（调试用，如 "Windows 10" 或 "Windows Server 2025"）')
    parser.add_argument("--min-age-hours", type=float, default=None,
                        help="清单比这个小时数新时直接跳过（定时任务节流用；默认不跳过）")
    args = parser.parse_args()

    if args.min_age_hours is not None:
        age = manifest_age_hours(args.out)
        if age is not None and age < args.min_age_hours:
            print("上次同步于 %.1f 小时前（阈值 %.0f 小时），本次跳过。" % (age, args.min_age_hours))
            return 0

    if not os.path.isfile(VENDORED):
        print("ERROR: 未找到内置工具：%s" % VENDORED)
        return 1
    bash = find_bash()
    if not bash:
        print("ERROR: 未找到 bash。Windows 请安装 Git for Windows（Git Bash）；Linux 自带。")
        return 1

    fido_targets = TARGETS
    fw_targets = FW_LINK_TARGETS
    if args.only:
        wanted = args.only.lower()
        fido_targets = [t for t in TARGETS if t[0].lower() == wanted]
        fw_targets = [t for t in FW_LINK_TARGETS if t[0].lower() == wanted]
        if not fido_targets and not fw_targets:
            all_keys = [t[0] for t in TARGETS] + [t[0] for t in FW_LINK_TARGETS]
            print("ERROR: --only 只支持：%s" % "、".join(all_keys))
            return 1

    results = {}

    # ---- Fido 家族（Windows 11 / Windows 10）----
    for key, edition_ids in fido_targets:
        print("==> 正在获取微软官方链接（Fido）：%s（EditionId=%s，语言 %s）"
              % (key, edition_ids, args.lang))
        try:
            url = fetch_target(bash, key, edition_ids, args.lang)
        except RuntimeError as exc:
            print("    [失败] %s" % exc)
            print("    提示：被微软短期限流（SentinelReject）时等 10-30 分钟再跑，不要连续重试。")
            return 2
        print("    选取直链：%s" % url)
        results[key] = url

    # ---- fwlink 家族（Windows Server 各版本）----
    # Server 短链比 Fido 更脆（部分版本只有单一 CDN、HEAD 可能偶发超时），
    # 所以单个失败只跳过该键，不影响 Windows 11/10 的刷新结果。
    for key, url in fw_targets:
        print("==> 正在解析 fwlink：%s" % key)
        try:
            final = fetch_fwlink(bash, key, url)
        except RuntimeError as exc:
            print("    [跳过] %s" % exc)
            continue
        print("    选取直链：%s" % final)
        results[key] = final

    if not results:
        print("ERROR: 所有目标都失败了，未产生任何直链。")
        return 2

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
