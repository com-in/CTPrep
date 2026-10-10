# CTPrep

Windows 重装工具。准备阶段在当前系统里跑（WPF 界面，新手 / 高级两种模式），重启进 WinPE 后自动分区、应用镜像、重建引导；第一次开机时装好驱动、清掉临时文件。不用自己敲 diskpart / dism / bcdboot。

[![build](https://github.com/com-in/CTPrep/actions/workflows/build.yml/badge.svg)](https://github.com/com-in/CTPrep/actions/workflows/build.yml)

## 工作流程

1. **准备**（当前系统里跑，要管理员权限）——探测系统版本和固件类型，拿到镜像（下载或用本地文件），腾出一块暂存分区，把镜像、PE 和部署脚本写进去，最后在 BCD 注册一个一次性的 WinPE 启动项，重启。
2. **部署**（WinPE 里自动跑）——diskpart 按方案处理目标磁盘（保留现有分区或整盘重建），dism 应用镜像并注入驱动，bcdboot 重建引导。之后删掉临时启动项、回收暂存分区，重启进新系统。
3. **收尾**（第一次开机）——装上暂存的驱动包、配置账户策略、清理临时文件。

## 功能

- 全新安装或保留文件二选一，也能整盘重建分区表（GPT / MBR）。系统可以装到另一块磁盘上。
- 镜像用本地文件（ISO / WIM / ESD）或 http(s) 直链，直链支持断点续传和 SHA256 校验。
- 镜像和 PE 的地址可以只写一个 `.json` 清单地址，程序下载前先取清单、按本机版本挑链接。以后换源改清单就行，程序不用动。
- 无人值守：OOBE 跳过，账户建好，用户名沿用当前的；密码、计算机名、时区都能设，也能跳过 Win11 的联网要求。
- 驱动包在 `[Drivers]` 里配 zip / 7z / exe，第一次开机时装上。
- 高级设置里能改目标磁盘、引导分区要不要格式化（默认开）、Defender 要不要禁用（默认关）、时区（下拉选）、演练模式。
- 主界面中英双语。PE 里是原生 x64 界面，不依赖 .NET，进度是 DISM 报的实际百分比。

## 环境要求

- Windows 7 / 8 / 8.1 / 10 / 11 x64，需要管理员权限（会修改 BCD 与磁盘分区）；
  Windows 7 上缺 Storage 模块与 ISO 挂载能力，这两处会自动退回 WMI 与内置的 ISO 解析器（详见「Windows 7 上的差异」）；
- 暂存空间默认 12 GB（`StagingSizeMB` 可调）；
- 两种发布版：绿色版自带 .NET 8 运行时；轻量版体积更小，但需要目标机器已装 .NET 8 桌面运行时（Windows Desktop Runtime）。

## Windows 7 上的差异

Win7 缺少的东西分两类，都得补。

**一、系统组件：Universal CRT。** Win7 不自带它，缺了进程在加载阶段就起不来，报
「无法启动此程序，因为计算机中丢失 api-ms-win-crt-runtime-l1-1-0.dll」。这个错误发生在
程序自己的代码跑起来之前，所以在程序里做检测或提示都没用，只能让它别缺。

发布版已经把整套 UCRT（`ucrtbase.dll` 加 42 个 `api-ms-win-*.dll` 转发器）放在
`CTPrep.exe` 同目录，即 Microsoft 文档的 app-local 部署方式，**不必先装 KB2999226**。
Windows 10/11 由系统 API set 解析到自带版本，这些副本不会被使用。来源与许可见
`redist/ucrt/README.md`。

> 如果这台 Win7 仍然报缺失，装一次 **KB2999226**（或 Visual C++ 2015-2022 可再发行组件包 x64）
> 即可，装完可以把那批文件删掉——那是 Microsoft 的首选做法，只是需要手动做一步。

**二、系统能力。** Win7 没有 PowerShell 的 Storage 模块（`Get-Disk` / `Resize-Partition` 那套），
也挂不了 ISO（`Mount-DiskImage`）。程序启动时会探测这两项能力，缺哪个就走对应的回退路径，
Win8 及以上完全不受影响：

| 环节 | Win8 及以上 | Win7 |
| --- | --- | --- |
| 磁盘与分区探测 | Storage 模块 | WMI（`Win32_DiskDrive` / `Win32_DiskPartition`） |
| 准备暂存分区 | `Resize-Partition` + `New-Partition` | diskpart `shrink` + `create partition` |
| 从 ISO 取 install.wim | 挂载后复制 | 内置解析器直接读（UDF，另带 ISO 9660 兜底） |

两点已知差异：

- **GPT 磁盘上的分区号**：WMI 不报告 MSR（微软保留分区），程序靠分区之间那段 16 MB 的空隙把它补回来。
  已在含 MSR 的 GPT 盘上和 Storage 模块的结果逐项比对过（分区号、盘符、大小、ESP/引导标记全部一致），
  但 Win7 真机上还没跑过——如果你在 Win7 + UEFI 的机器上遇到目标分区选错，把日志发我。
- **BitLocker**：Win7 没有 BitLocker 的 PowerShell 模块，探测不到时按「未知」处理，不会拦住部署。
  若系统盘确实加密了，请先用 `manage-bde -protectors -disable C:` 暂停保护。

## 使用

1. 准备一份发布版（绿色版或轻量版，见「从源码构建」），整个文件夹一起拷到目标机器，不要只拷 exe；
2. 以管理员身份运行 `CTPrep.exe`；
3. 新手模式一路点确认就行，要改磁盘、镜像、账户就进高级模式；
4. 重启后不用再操作，等它装完。

第一次使用建议先跑一遍演练模式（`DryRun=1` 或高级设置里的开关）：只打印将要执行的命令，不改磁盘。

## 配置

- `config.ini` —— 新手模式的来源与行为：`[PE]`、`[Image]`（按系统版本键匹配，没命中就回退 `Default`）、`[Drivers]`、`[Deploy]`（暂存分区大小、卷标、默认安装方式、时区等）。下载的东西统一放进 `RuntimeDir`。
- `custom.ini` —— 高级设置的初值和下载来源，和 `config.ini` 各管各的；不存在时会自动从 `config.ini` 复制一份出来。
- 填地址的地方，本地路径、http(s) 直链、`.json` 清单地址都认。清单怎么写、怎么部署，见 `docs/README.md`。

## 从源码构建

需要 .NET 8 SDK；PE 界面 EXE 已入库，正常构建不需要 Zig。

```powershell
# 绿色版：自包含 .NET 8 运行时
dotnet publish src/CtPrep.App/CtPrep.App.csproj -p:PublishProfile=Green-x64

# 轻量版：依赖目标机器已安装 .NET 8 桌面运行时
dotnet publish src/CtPrep.App/CtPrep.App.csproj -p:PublishProfile=Lite-x64
```

两份产物（`publish-green/`、`publish-lite/`）都自带 PE 资源（`runtime/pe`）。修改 PE 界面、重建 `boot.wim`、字体处理等见 `PE-UPDATE.md`；CI 在每次 push / PR 时自动构建，产物见 Actions 页面。

## 发布

推送形如 `v1.2.3` 的 tag 才会发布 Release，且要求 tag 版本与 csproj 的 `<Version>` 一致、`docs/images.json` 里已有真实镜像地址，否则工作流会在发布前停下。普通提交不会发布。完整规则见 `RELEASE.md`。

## 目录

```text
src/CtPrep.App/    主程序；Assets/pe 为 PE 侧脚本模板，Assets/i18n 为界面文案
src/CtPrep.PeUi/   WinPE 内的原生界面（C 编写、Zig 编译，EXE 已入库）
runtime/pe/        PE 运行时资源（boot.wim 经 Git LFS 存储）
docs/              官网与链接清单（GitHub Pages 发布）
tools/             PE 精简、界面编译、字体生成等脚本
```

## 注意事项

- 全新安装 / 整盘重建会清空目标磁盘，操作前确认没有要保留的数据；
- 会改动分区表和系统引导，建议先在有快照的虚拟机里跑一遍；各功能的验证状态见 `PE-UPDATE.md`；
- 克隆本仓库需要 git-lfs（`boot.wim` 是 LFS 对象）。

## 许可

Apache-2.0，见 `LICENSE`。
