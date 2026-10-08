#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从微软官方下载渠道抓取最新 Windows 下载直链，更新 CTPrep 的链接清单。

微软官方直链约 24 小时后失效，建议每天执行一次（服务器 cron / Windows
任务计划 / GitHub Actions 都可以）。脚本只维护清单里的两个家族键
（"Windows 11" / "Windows 10"，家族前缀匹配自动覆盖各自所有子版本，
不用随版本号改）；你自建分发的旧系统条目、PE 条目等一律原样保留。

用法：
    python tools/sync-microsoft-links.py --out docs/images.json   # 更新清单
    python tools/sync-microsoft-links.py --print                  # 只打印，不改文件
    python tools/sync-microsoft-links.py --print --debug          # 附接口原始响应

仅依赖标准库（Python 3.8+）。
"""

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone

PROFILE = "606624d44113"
FP_ORG_ID = "y6jn8c31"
CONNECTOR = "https://www.microsoft.com/software-download-connector/api"
FP_TAGS = "https://vlscppe.microsoft.com/tags"
OVDF_BASE = "https://ov-df.microsoft.com"
INSTANCE_ID = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")

# 要同步的产品。key = 清单里由本脚本负责的家族键（覆盖整个系列，不随版本号变）；
# product_id 是微软自己的产品编号，会随大版本更换——换号时更新这里即可，
# 最新编号可从 pbatard/Fido 源码的 $WindowsVersions 表里查到。
PRODUCTS = [
    {"product_id": 3813, "page": "windows11", "key": "Windows 11"},  # 26H2 多版本 ISO（x64）
    {"product_id": 2618, "page": "windows10", "key": "Windows 10"},  # 22H2 多版本 ISO（x64）
]

# 语言代码 -> (接口里的英文语言名, 下载文件名里可能的语言标记)
LANG_NAMES = {
    "zh-cn": ("Chinese (Simplified)", ("chinese_simplified", "zh-cn")),
    "en-us": ("English (International)", ("english_international", "en-us")),
}

RELEASE_RE = re.compile(r"Win\d+_([0-9]{2}H[12])_", re.IGNORECASE)
BUILD_RE = re.compile(r"_(\d{4,6})_(\d{2,6})\.iso", re.IGNORECASE)


def build_opener():
    import http.cookiejar
    jar = http.cookiejar.CookieJar()
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def http_get(opener, url, timeout=45, headers=None):
    all_headers = {"User-Agent": UA}
    if headers:
        all_headers.update(headers)
    req = urllib.request.Request(url, headers=all_headers)
    with opener.open(req, timeout=timeout) as resp:
        return resp.read().decode("utf-8-sig", "replace")


def http_get_json(opener, url, timeout=45, headers=None):
    return json.loads(http_get(opener, url, timeout, headers))


def detect_release(url):
    match = RELEASE_RE.search(url)
    if match:
        return match.group(1).upper()
    build = BUILD_RE.search(url)
    return "%s.%s" % build.groups() if build else ""


class SentinelReject(RuntimeError):
    """微软风控拒绝（SentinelReject）；换一个新会话重试通常就能过。"""


def check_sentinel(result):
    for err in (result.get("Errors") or []):
        if "SentinelReject" in str(err.get("Key", "")):
            raise SentinelReject(str(err.get("Value") or "SentinelReject"))


def handshake(opener, session):
    """微软下载保护要求的两步握手：vlscppe 白名单 + ov-df 校验。
    参照 Fido 现役实现；任一步失败只告警，以链接接口的真实响应为准。"""
    try:
        http_get(opener, "%s?org_id=%s&session_id=%s" % (FP_TAGS, FP_ORG_ID, session))
    except Exception as exc:  # noqa: BLE001
        print("    [warn] vlscppe 握手失败（继续尝试）：%s" % exc, file=sys.stderr)

    try:
        text = http_get(opener, "%s/mdt.js?instanceId=%s&PageId=si&session_id=%s"
                        % (OVDF_BASE, INSTANCE_ID, session))
        w = re.search(r"[?&]w=([A-Fa-f0-9]+)", text)
        rticks = re.search(r'rticks="\+?(\d+)', text)
        if not w or not rticks:
            raise RuntimeError("mdt.js 响应里没有 w/rticks")
        http_get(opener, "%s/?session_id=%s&CustomerId=%s&PageId=si&w=%s&mdt=%d&rticks=%s"
                 % (OVDF_BASE, session, INSTANCE_ID, w.group(1),
                    int(time.time() * 1000), rticks.group(1)))
    except Exception as exc:  # noqa: BLE001
        print("    [warn] ov-df 握手失败（继续尝试）：%s" % exc, file=sys.stderr)


def fetch_product_once(opener, product, lang, debug=False):
    """按 Fido 现役同款的官方接口流程取回直链（单次尝试）。"""
    session = str(uuid.uuid4())

    handshake(opener, session)

    sku_url = ("%s/getskuinformationbyproductedition?profile=%s&productEditionId=%s"
               "&SKU=undefined&friendlyFileName=undefined&Locale=%s&sessionID=%s"
               % (CONNECTOR, PROFILE, product["product_id"], lang, session))
    sku_info = {}
    skus = []
    for attempt in range(3):
        if attempt:
            time.sleep(2)
        sku_info = http_get_json(opener, sku_url)
        skus = sku_info.get("Skus") or sku_info.get("SKUs") or []
        if skus:
            break
    if debug:
        print(json.dumps(sku_info, ensure_ascii=False, indent=2)[:2400], file=sys.stderr)
    if not skus:
        check_sentinel(sku_info)
        raise RuntimeError("接口没有返回 SKU（原始响应见 --debug）：%s" % sku_info)

    lang_full, lang_tokens = LANG_NAMES.get(lang, (lang, (lang,)))
    picked = None
    for sku in skus:
        names = " ".join(str(sku.get(k, "")) for k in ("Language", "LocalizedLanguage", "Name"))
        if lang_full.lower() in names.lower():
            picked = sku
            break
    if picked is None:
        picked = skus[0]
        if len(skus) > 1:
            print("    [warn] 没找到「%s」的 SKU，暂用列表第一个" % lang_full, file=sys.stderr)

    # 两个细节照抄 Fido 现役实现：productEditionId 要传字面量 undefined；
    # 且必须带 Referer（产品页），否则微软的风控会拒（SentinelReject）。
    link_url = ("%s/GetProductDownloadLinksBySku?profile=%s&productEditionId=undefined"
                "&SKU=%s&friendlyFileName=undefined&Locale=%s&sessionID=%s"
                % (CONNECTOR, PROFILE, picked.get("Id"), lang, session))
    referer = "https://www.microsoft.com/software-download/%s" % product["page"]
    result = http_get_json(opener, link_url, headers={"Referer": referer})
    check_sentinel(result)
    if debug:
        print(json.dumps(result, ensure_ascii=False, indent=2)[:2400], file=sys.stderr)

    # 响应字段随接口版本变化：现在返回 ProductDownloadOptions（URL 在 Uri 里，
    # DownloadType 是数字=架构），旧格式是 ProductDownloadLinks / DownloadUrl。两种都认。
    options = (result.get("ProductDownloadOptions")
               or result.get("ProductDownloadLinks") or [])
    if not options:
        raise RuntimeError("接口没有返回任何下载链接（原始响应见 --debug）：%s" % result)

    def option_url(item):
        return str(item.get("Uri") or item.get("DownloadUrl") or item.get("Url") or "")

    def takes(items, token):
        return [item for item in items if token.lower() in option_url(item).lower()]

    def has_lang(items):
        return [item for item in items
                if any(token in option_url(item).lower() for token in lang_tokens)]

    chosen = None
    for candidates in (has_lang(takes(options, "x64")),
                       has_lang(options),
                       takes(options, "x64"),
                       options):
        if candidates:
            chosen = candidates[0]
            break

    url = option_url(chosen)
    if not url:
        raise RuntimeError("链接条目里没有 URL：%s" % chosen)
    return {"url": url, "release": detect_release(url),
            "language": chosen.get("Language") or lang,
            "sku": picked.get("Name") or picked.get("LocalizedLanguage") or ""}


def fetch_product(opener, product, lang, debug=False, attempts=3):
    """带重试：被风控拒绝（SentinelReject）时换新会话重来。"""
    last = None
    for index in range(attempts):
        if index:
            time.sleep(5)
        try:
            return fetch_product_once(opener, product, lang, debug)
        except SentinelReject as exc:
            last = exc
            print("    [warn] 第 %d/%d 次被风控拒绝，稍后重试" % (index + 1, attempts), file=sys.stderr)
    raise last


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
    parser = argparse.ArgumentParser(description="同步微软官方 Windows 下载链接到 CTPrep 链接清单")
    parser.add_argument("--out", default="docs/images.json", help="要更新的清单文件（默认 docs/images.json）")
    parser.add_argument("--lang", default="zh-cn", help="语言代码，如 zh-cn / en-us（默认 zh-cn）")
    parser.add_argument("--print", dest="print_only", action="store_true",
                        help="只打印抓到的链接，不改文件")
    parser.add_argument("--debug", action="store_true", help="打印接口原始响应")
    args = parser.parse_args()

    opener = build_opener()
    entries = {}
    for index, product in enumerate(PRODUCTS):
        if index:
            time.sleep(2)
        print("==> 正在获取微软官方链接：%s（%s）" % (product["page"], args.lang))
        info = fetch_product(opener, product, args.lang, args.debug)
        tag = ("（%s）" % info["release"]) if info["release"] else ""
        print("    %s%s -> %s" % (product["key"], tag, info["url"][:130]))
        entries[product["key"]] = info

    if args.print_only:
        print()
        print(json.dumps({key: {"url": value["url"]} for key, value in entries.items()},
                         ensure_ascii=False, indent=2))
        return 0

    manifest = load_manifest(args.out)
    existing = manifest.get("images") or {}
    merged = {}
    # 自动键放最前：程序的家族前缀匹配取「文件序第一个」命中的键，
    # 这样非精确命中的机器优先拿到官方最新版；自建条目原样排在后面。
    for key, info in entries.items():
        merged[key] = {"url": info["url"], "sha256": ""}
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
    sys.exit(main())
