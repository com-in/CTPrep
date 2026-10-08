# docs — 官网与链接清单

这个目录有两个用途，推荐直接用 **GitHub Pages 的 `/docs` 方式**发布（Cloudflare Pages 等任意静态托管也行）：

| 文件 | 用途 |
| --- | --- |
| `index.html` | CTPrep 官网落地页（纯静态，无依赖、无构建步骤） |
| `images.json` | 「链接清单」— 程序在下载前请求它，获取镜像与 PE 的真实链接 |
| `_headers` | Cloudflare Pages 专用响应头（清单短缓存 60 秒）；GitHub Pages 会原样发布该文件、不生效、无副作用 |
| `README.md` | 本文档 |

## 链接清单是什么

程序在开始部署前，先请求清单地址拿到真正的下载链接：

```
CTPrep.exe ──请求──▶ https://<你的域名>/images.json ──▶ 按本机版本挑条目 ──▶ 下载镜像 / PE
```

好处：

- **换托管、换链接不用更新程序**：镜像从 A 网盘换到 B 对象存储，只需改 `images.json` 重新部署一次；
- **一处维护所有链接**：镜像各版本 + PE（`boot.wim`）都写在同一个文件里；
- **校验值随链接走**：每条链接可带 `sha256`，下载完成后自动校验。

只要地址以 `.json` 结尾，程序就会按清单处理；镜像本体（`.iso` / `.wim` / `.esd`）不受影响。

## 清单格式

```json
{
  "schema": 1,
  "updated": "2026-10-08",
  "images": {
    "Windows 11 24H2": { "url": "https://…/win11_24h2_x64.iso", "sha256": "…" },
    "Windows 10 22H2": { "url": "https://…/win10_22h2_x64.iso", "sha256": "" },
    "Default":         { "url": "https://…/win_x64.iso",        "sha256": "" }
  },
  "pe": {
    "url": "https://…/CTPrep_PE.wim",
    "sha256": ""
  }
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schema` | 否 | 格式版本，当前为 1 |
| `updated` | 否 | 仅用于排查（日志/网页展示），格式随意 |
| `images` | 与 `pe` 至少其一 | 镜像条目，键为**版本匹配键** |
| `images.<键>.url` | 是 | 直链，支持 `.iso` / `.wim` / `.esd`；必须支持 Range（断点续传） |
| `images.<键>.sha256` | 否 | 小写十六进制；留空 = 跳过校验 |
| `pe.url` / `pe.sha256` | 否 | WinPE（`boot.wim`）的链接；镜像走本地文件时也需要它 |

匹配规则（与 `config.ini` 的 `[Image]` 段完全一致）：

1. **精确命中**：键与本机探测结果相同（如 `Windows 11 24H2`，探测逻辑见程序日志「镜像匹配键」）；
2. **家族前缀**：忽略版本号，取第一个以家族名开头的键（如 `Windows 11`）；
3. **Default**：以上都未命中时的回退项。

## 程序侧怎么启用

**新手模式**（`config.ini`，主程序同目录）：

```ini
[Image]
Default=https://<你的域名>/images.json
```

也可以只给某个版本键填清单地址，其余键留空——未命中时先走家族前缀 / Default。

```
[PE]
LightningPE=https://<你的域名>/images.json
```

`[PE]` 段同样支持清单地址：程序会从清单的 `pe` 条目取 PE 链接。

**高级设置**（界面）：把清单地址直接粘进「系统镜像地址」即可；「分析镜像」对清单地址同样有效（会先按本机版本解析再下载分析）。

程序日志里会留下关键行，便于排查：

- `正在获取链接清单：…`
- `链接清单已解析镜像地址：…` / `链接清单已解析 PE 地址：…`
- `链接清单条目没有 sha256，将跳过完整性校验。`（警告）
- 失败时：`链接清单无效（…）` / `链接清单里没有与本机版本（…）匹配的镜像…`

## 部署

### 方式 A：GitHub Pages（推荐，零配置）

1. 把仓库推到 GitHub；
2. 仓库 **Settings → Pages** → Source 选 **Deploy from a branch** → 分支选 `main`、目录选 **`/docs`** → Save；
3. 等 1-2 分钟，站点地址为：

   - 官网：`https://<用户名>.github.io/<仓库名>/`
   - 清单：`https://<用户名>.github.io/<仓库名>/images.json`

此后每次修改 `docs/` 下的文件并推送，站点自动更新（GitHub Pages 默认缓存约 10 分钟，清单更新最多 10 分钟后生效）。

> 公开仓库免费使用；私有仓库需要 GitHub Pro。
> `github.io` 在国内一般可访问、偶尔不稳；如果程序拉清单不稳定，改用下面的 Cloudflare Pages 或对象存储托管同一份 `images.json` 即可。

### 方式 B：Cloudflare Pages

1. Cloudflare 控制台 → Workers 和 Pages → 创建 → Pages → 连接 Git 仓库；
2. 构建配置：**框架预设** None、**构建命令**留空、**构建输出目录** `docs`；
3. 保存并部署。清单缓存按 `_headers` 配置（60 秒）生效。

也可以不用 Git：`npx wrangler pages deploy docs --project-name ctprep`，或把 `docs/` 里的文件直接拖进控制台。

部署完成后（以实际项目名为准）：

- 官网：`https://<项目名>.pages.dev/`
- 清单：`https://<项目名>.pages.dev/images.json`

> 自定义域名可选：在 Pages 项目里绑定即可（CF 托管域名免备案，免费）。

## 自动同步微软官方链接（可选）

微软官方直链约 24 小时后失效，所以指向微软的清单条目需要定期刷新。仓库自带一条工具链——抓取部分**复用**了 [caichengjie/microsoft-iso-directlink](https://github.com/caichengjie/microsoft-iso-directlink)（MIT 协议，已内置到 `tools/microsoft-iso-directlink/`）：

```bash
python tools/sync-microsoft-links.py --print                  # 预演：只打印抓到的链接，不改文件
python tools/sync-microsoft-links.py                          # 正式更新 docs/images.json
```

- 只更新它负责的两个家族键（`Windows 11`、`Windows 10`），且放在 `images` 最前，保证家族匹配优先命中最新版；
- 自建分发的条目（Win8.1 / Win7、PE 等）原样保留；
- 全部成功才写文件，任何一步失败都不动旧清单——旧清单继续可用。

定时方式任选（Windows 上需要 Git for Windows 提供 bash）：

- **服务器（推荐）**——crontab 加一行，每天跑一次：
  `30 20 * * * cd /srv/ctprep && python3 tools/sync-microsoft-links.py`
- **Windows 任务计划**——每天执行一次 `python tools\sync-microsoft-links.py`；
- **GitHub Actions**——加一个 schedule 工作流，跑完把清单提交回仓库。

两个注意点：

- 微软每次发大版本可能**更换 EditionId**：脚本顶部 `TARGETS` 里是当前候选（按序尝试，命中即用）；核查最新编号用 `bash tools/microsoft-iso-directlink/get_microsoft_iso_link.sh list`，或看 [pbatard/Fido](https://github.com/pbatard/Fido) 源码的 `$WindowsVersions` 表。
- 报 `SentinelReject` 是微软按 IP 短期限流：每天 1 次的频率没有压力；被拒时等 10-30 分钟再跑，不要连续重试。

## 日常维护

1. **更新链接**：改 `docs/images.json` → 推送；GitHub Pages 约 10 分钟内、Cloudflare Pages 约 60 秒内生效；
2. **换镜像源**：把 `url` 指向新的托管即可（对象存储 / GitHub Releases / 自建均可，要求同上：直链 + Range）；
3. **PE 也可以放清单里**：`pe.url` 指向任意可直连的托管（如 GitHub Releases 资产——单文件上限 2 GB）；仓库里的 `runtime/pe/boot.wim` 走 Git LFS，供源码构建与发布使用，不建议直接当下载源（raw 链接又慢又不稳）；
4. **校验值**：镜像上游更新后记得同步 `sha256`，否则程序会在下载完成后报「SHA256 校验失败」。

## 官网维护

`index.html` 里的链接已指向本仓库（`https://github.com/com-in/CTPrep`），仓库改名/迁移时全局替换即可：

- `https://github.com/com-in/CTPrep`（3 处）：仓库主页；
- `https://github.com/com-in/CTPrep/releases`（2 处）：绿色版下载地址（发 Release 后自动有效）。

本地预览：直接双击 `index.html` 即可，或 `python -m http.server 8080 -d docs`。

页面不引用任何外部资源（无 CDN、无字体、无 JS 依赖），改完直接部署。

## 注意

- 请勿在本清单或官网中直接托管 Windows 系统镜像本体（云平台/仓库对版权内容有限制，且容易失效）；清单只放**链接**，镜像托管在你自己选择的对象存储上，个人自用。
- 清单里的 `url` 必须是**直链**（点开就开始下载），网盘的分享页地址不行；百度网盘 / 阿里云盘等无稳定直链的服务需要配合对象存储或自建服务。
