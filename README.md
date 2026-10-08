# CTPrep

Windows 重装工具。准备阶段在当前系统里运行（WPF 界面，新手 / 高级两种模式），重启进 WinPE 后自动完成分区、应用镜像和引导重建，部署完自动收尾，不用自己敲 diskpart / dism / bcdboot。

[![build](https://github.com/com-in/CTPrep/actions/workflows/build.yml/badge.svg)](https://github.com/com-in/CTPrep/actions/workflows/build.yml)

## 工作流程

1. **准备**（当前系统内，需管理员权限）——探测系统版本与固件类型；下载或用本地镜像；腾出一块暂存分区（压缩系统卷新建或复用现有分区），把镜像、PE 与部署脚本写进去；在 BCD 注册一次性 WinPE 启动项后重启。
2. **部署**（WinPE 内自动执行）——diskpart 处理目标磁盘（保留现有分区或整盘重建），dism 应用镜像、注入驱动，bcdboot 重建引导；删除临时引导项、回收暂存分区，重启进新系统。
3. **收尾**（首次开机自动）——安装暂存的驱动包、配置账户策略、清理临时文件。

## 功能

- 安装方式：全新安装 / 保留文件；可整盘重建（GPT / MBR），也可以把系统装到另一块磁盘；
- 镜像来源：本地 ISO / WIM / ESD，或 http(s) 直链（断点续传、SHA256 校验）；
- 链接清单：镜像与 PE 的地址可以只填一个 `.json` 清单地址，程序下载前先取清单、按本机版本挑链接——换链接不用改程序；
- 无人值守：自动走完 OOBE；账户名沿用当前系统，可设置密码、计算机名、时区，跳过 Win11 联网要求；
- 驱动包：`[Drivers]` 中配置 zip / 7z / exe，首次开机自动安装；
- 高级设置：目标磁盘、格式化引导分区（默认开）、禁用 Windows Defender（默认关）、时区下拉、演练模式；
- 主界面中英双语；PE 内为原生 x64 图形界面（不依赖 .NET），进度取自 DISM 实际百分比。

## 环境要求

- Windows 10 / 11 x64，需要管理员权限（会修改 BCD 与磁盘分区）；
- 暂存空间默认 12 GB（`StagingSizeMB` 可调）；
- 绿色版自包含 .NET 8 运行时，不需要另装。

## 使用

1. 准备绿色版（自行构建，或从 Actions 页面下载构建产物），整个文件夹一起拷到目标机器，不要只拷 exe；
2. 以管理员身份运行 `CTPrep.exe`；
3. 新手模式一路确认即可，或进高级模式调整磁盘、镜像、账户等；
4. 重启后不用再操作，等它装完。

第一次使用建议先跑一遍演练模式（`DryRun=1` 或高级设置里的开关）：只打印将要执行的命令，不改磁盘。

## 配置

- `config.ini` —— 新手模式的来源与行为：`[PE]`、`[Image]`（按系统版本键匹配，未命中回退 `Default`）、`[Drivers]`、`[Deploy]`（暂存分区大小、卷标、默认安装方式、时区等）；下载内容统一存到 `RuntimeDir`。
- `custom.ini` —— 高级设置的初值与下载来源，与 `config.ini` 相互独立；不存在时会自动从 `config.ini` 复制生成。
- 两个文件里凡是地址的位置，都支持本地路径、http(s) 直链、`.json` 链接清单三种写法；清单格式与部署方法见 `docs/README.md`。

## 从源码构建

需要 .NET 8 SDK；PE 界面 EXE 已入库，正常构建不需要 Zig。

```powershell
dotnet publish src/CtPrep.App/CtPrep.App.csproj -p:PublishProfile=Green-x64
```

产物在 `publish-green/`（自包含 win-x64，含 PE 资源）。修改 PE 界面、重建 `boot.wim`、字体处理等见 `PE-UPDATE.md`；CI 在每次 push / PR 时自动构建，产物见 Actions 页面。

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
