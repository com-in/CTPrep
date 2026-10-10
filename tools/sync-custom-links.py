#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从自建下载源取 Windows 7 / 8 / 8.1 的直链，合并进 CTPrep 链接清单。

自建源按 sha256 取链接（sha256 即文件的身份）：

    GET  {base}/api/filelink?sha256=<64 位十六进制>  取链接（不存在则自动创建）
    POST {base}/api/refresh                          重新生成（旧短码失效，计数保留）
    GET  {base}/api/info?sha256=<sha256>             查询文件与链接状态

Win11 / Win10 仍然由 sync-microsoft-links.py 抓微软官方直链，本脚本一个字都不碰：
两边各维护 tools/custom-sources.json 里列出的那几个键。自建源的链接同样约 24 小时
过期，所以也要定期刷新，节流规则和微软那条一致。

节流读的是清单里的 custom_updated，不能复用 updated：两条脚本若共用一个时间戳，
先跑的那条会把它刷成「刚刚」，后跑的那条就会永远判定为「还新」而跳过，等于永不刷新。

用法：
    python tools/sync-custom-links.py                 # 更新 docs/images.json
    python tools/sync-custom-links.py --print         # 只打印，不改文件
    python tools/sync-custom-links.py --only "Windows 7" --print

配置：
    tools/custom-sources.json      版本键 -> sha256（64 位十六进制）
    环境变量 CTPREP_LINK_API       自建源地址（默认 https://lf.epmc.qzz.io/）

sha256 没填时只是跳过，不会让流程失败——方便先把脚本接进来、之后再填值。

依赖：Python 3.8+（只用标准库，不需要 bash / curl）。
"""

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SOURCES = os.path.join(HERE, "custom-sources.json")
DEFAULT_BASE = "https://lf.epmc.qzz.io/"

SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")
TIMEOUT = 30


def load_sources(path):
    """读自建源配置，返回 [(版本键, sha256)]；sha256 未填的条目跳过。"""
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
    except FileNotFoundError:
        print("跳过：未找到自建源配置 %s" % path)
        return []
    except json.JSONDecodeError as exc:
        print("跳过：自建源配置不是合法 JSON（%s）：%s" % (path, exc))
        return []

    if not isinstance(data, dict):
        print("跳过：自建源配置的根节点不是对象：%s" % path)
        return []

    items = []
    for key, sha in data.items():
        value = str(sha or "").strip()
        if not SHA256_RE.match(value):
            print("    跳过 %s：sha256 还没填（需要 64 位十六进制），当前是 %r" % (key, value))
            continue
        items.append((key, value.lower()))
    return items


def extract_link(body):
    """从响应里取链接：兼容 {"url":...} / {"link":...} / {"data":{...}} / 纯文本。"""
    text = (body or "").strip()
    if not text:
        return None
    if text.startswith("http"):
        return text.split()[0]

    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        return None

    if isinstance(payload, str):
        return payload if payload.startswith("http") else None
    if not isinstance(payload, dict):
        return None

    for key in ("url", "link", "downloadUrl", "download_url", "href", "filelink", "data"):
        value = payload.get(key)
        if isinstance(value, str) and value.startswith("http"):
            return value
        if isinstance(value, dict):
            for sub in ("url", "link", "downloadUrl", "download_url"):
                inner = value.get(sub)
                if isinstance(inner, str) and inner.startswith("http"):
                    return inner
    return None


def describe_error(exc):
    """把服务返回的错误体读出来：{"error":...,"message":...} 优先取 message。

    只看 HTTP 状态码太难排查——比如 404 可能只是「这个 sha256 还没登记文件」。
    """
    try:
        raw = exc.read().decode("utf-8", errors="replace")
    except Exception:
        return str(exc)
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        return raw.strip()[:200]
    if isinstance(payload, dict):
        return str(payload.get("message") or payload.get("error") or raw)[:200]
    return raw.strip()[:200]


def fetch_link(base, sha256):
    """调 /api/filelink 取一个 sha256 对应的链接；失败抛 RuntimeError。"""
    url = "%s/api/filelink?%s" % (
        base.rstrip("/"),
        urllib.parse.urlencode({"sha256": sha256}),
    )
    request = urllib.request.Request(url, headers={"Accept": "application/json, text/plain"})
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        raise RuntimeError("HTTP %d：%s" % (exc.code, describe_error(exc)))
    except (urllib.error.URLError, OSError) as exc:
        raise RuntimeError("请求失败 %s（%s）" % (url, exc))

    link = extract_link(body)
    if not link:
        raise RuntimeError("响应里解析不到链接：%s" % body[:200])
    return link


def load_manifest(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except json.JSONDecodeError as exc:
        raise SystemExit("清单不是合法 JSON（%s）：%s" % (path, exc))


def custom_age_hours(path, field):
    """清单里自建源时间戳距今的小时数；缺失或无法解析时返回 None。"""
    stamp = str(load_manifest(path).get(field) or "").strip()
    if not stamp:
        return None
    try:
        moment = datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        return None
    return (datetime.now(timezone.utc) - moment).total_seconds() / 3600.0


def main():
    parser = argparse.ArgumentParser(description="从自建下载源取直链并合并进 CTPrep 链接清单")
    parser.add_argument("--out", default="docs/images.json", help="要更新的清单文件（默认 docs/images.json）")
    parser.add_argument("--sources", default=DEFAULT_SOURCES, help="自建源配置（默认 tools/custom-sources.json）")
    parser.add_argument("--base-url", default=None,
                        help="自建源地址；默认取环境变量 CTPREP_LINK_API，再退回 http://localhost:3000")
    parser.add_argument("--print", dest="print_only", action="store_true", help="只打印，不改文件")
    parser.add_argument("--only", default=None, help='只处理指定键（调试用，如 "Windows 7"）')
    parser.add_argument("--min-age-hours", type=float, default=None,
                        help="自建源时间戳比这个小时数新时直接跳过（定时任务节流用）")
    args = parser.parse_args()

    # 地址有内置默认值，只有想临时换源时才需要 --base-url 或 CTPREP_LINK_API
    base = ((args.base_url or os.environ.get("CTPREP_LINK_API") or "").strip()
            or DEFAULT_BASE)
    print("自建源地址：%s" % base)

    sources = load_sources(args.sources)
    if args.only:
        sources = [item for item in sources if item[0].lower() == args.only.lower()]
        if not sources:
            print("跳过：--only 指定的键在自建源配置里没有可用的 sha256")
            return 0
    if not sources:
        print("跳过：自建源配置里没有填好 sha256 的条目")
        return 0

    if args.min_age_hours is not None:
        age = custom_age_hours(args.out, "custom_updated")
        if age is not None and age < args.min_age_hours:
            print("自建源上次同步于 %.1f 小时前（阈值 %.0f 小时），本次跳过。" % (age, args.min_age_hours))
            return 0

    results = {}
    failed = False
    for key, sha256 in sources:
        print("==> 正在取自建源链接：%s（sha256=%s…）" % (key, sha256[:12]))
        try:
            url = fetch_link(base, sha256)
        except RuntimeError as exc:
            print("    [失败] %s" % exc)
            failed = True
            continue
        print("    取得链接：%s" % url)
        results[key] = (url, sha256)

    if args.print_only:
        print()
        print(json.dumps({key: {"url": url, "sha256": sha} for key, (url, sha) in results.items()},
                         ensure_ascii=False, indent=2))
        return 2 if failed else 0

    if not results:
        print("没有任何条目取到链接，清单保持不变。")
        return 2 if failed else 0

    manifest = load_manifest(args.out)
    images = manifest.get("images") or {}
    for key, (url, sha256) in results.items():
        # source 只是给人看的标记；程序解析清单时忽略未知字段。
        images[key] = {"url": url, "sha256": sha256, "source": "custom"}
        print("    已写入清单：%s" % key)
    manifest["images"] = images
    manifest["schema"] = int(manifest.get("schema") or 1)
    # 只写自己的时间戳，绝不碰 updated（那是微软那条脚本的节流依据）
    manifest["custom_updated"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print("清单已更新：%s" % args.out)
    return 2 if failed else 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
