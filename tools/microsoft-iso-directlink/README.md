# microsoft-iso-directlink（内置第三方工具）

本目录是 [caichengjie/microsoft-iso-directlink](https://github.com/caichengjie/microsoft-iso-directlink) 的**内置副本**（vendored），给 `tools/sync-microsoft-links.py` 提供「微软官方直链生成」能力。

- 上游版本：commit `35b749767ec73c41f2f4be1571e08284720cd054`（2026-08-14）
- 许可证：MIT（见同目录 `LICENSE`，版权归 caichengjie）
- 原理：复刻 [pbatard/Fido](https://github.com/pbatard/Fido) 的流程 —— vlscppe 白名单 + ov-df 验证 + software-download-connector 接口；拿到的直链约 **24 小时**有效。

## 与上游的唯一差异（本地补丁）

只加了一处：**自动挑选可用的 Python 3 解释器**（优先 `python3`、回退 `python`；Linux 服务器上通常没有 `python` 命令）。补丁在文件头部有 `[CTPrep vendored copy]` 标注。

## 手动用法（临时拿一条链接）

```bash
cd tools/microsoft-iso-directlink

# 列出支持的版本（EditionId）
bash get_microsoft_iso_link.sh list

# 拿 Win10 22H2 简体中文 的全部架构直链（x64 排最前）
bash get_microsoft_iso_link.sh 2618 简体

# 拿 Win11 多版本 简体中文（3321 是脚本内置矩阵里的编号）
bash get_microsoft_iso_link.sh 3321 简体

# 全自动：生成直链 + 下载 x64 + 校验大小（会下载 5-8GB，注意磁盘）
bash get_microsoft_iso_link.sh auto 2618 简体
```

依赖：`bash` + `curl` + `python3`（Windows 用 Git Bash / WSL）。

## 更新这个内置副本

上游改动时：重新下载其 `scripts/get_microsoft_iso_link.sh` 覆盖本目录同名文件，再按文件头部的 `[CTPrep vendored copy]` 注释重新应用 Python 解释器补丁即可（改动点见上）。

## 注意

- 报 `SentinelReject` = 微软按 IP 短期限流：等 10-30 分钟再试，**不要连发**（脚本自带 3 次自动重试）。
- 直链 24 小时过期属正常现象，重跑一次就有新的。
- EditionId 会随微软换版变化；本目录脚本内置的版本矩阵可能滞后，最新编号可参考 Fido 源码的 `$WindowsVersions` 表。
