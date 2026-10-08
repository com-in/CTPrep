# PE 图形安装界面更新

## BCD 引导项注册修复（2026-10-08）

现象：准备阶段弹出 `bcdedit /set {ramdiskoptions} ramdisksdidevice partition=T: 执行失败（退出码 1）／尝试引用指定项时出错。系统找不到指定的文件。`

原因：旧实现把 ramdisk 设备选项写在众所周知的别名对象 `{ramdiskoptions}` 上。该对象是**共享**配置（WinRE 等也用它），而且在很多系统上**根本不存在**；`bcdedit` 对「别名 + /set」的失败只回一句看不出原因的话，`/enum` 探测也不可靠，于是流程直接中断。

修复：`BootService.CreateRamdiskEntryAsync` 改为

1. 先 `/enum {bootmgr}` 探测实时引导库是否可打开，打不开就直接给出可读原因；
2. 用随机 GUID 新建**私有**设备选项对象（`/create {GUID} /d "CTPrep Ramdisk" /device`），不覆盖 `{ramdiskoptions}`；
3. 创建后立即 `/enum` 回读校验；拿不到对象则回退到 `{ramdiskoptions}`（仅在不存在时创建），并记录警告；
4. 必需属性写完后再次回读校验引导项确实带上了 `ramdisk` 与 `winpe`，避免带着进不去的引导项重启；
5. 报错信息改为「命令 + 退出码 + 原始输出 + 当前步骤 + 排查建议」，不再只抛一行 bcdedit 原文；
6. 私有设备选项对象在失败回滚（宿主侧）与部署结束（PE 侧 `deploy.cmd`）时一并删除；回退到共享别名时不删。

**注意：修复后的二进制必须重新复制到虚拟机**，旧 `publish-green`（10-08 00:17 及更早）仍会复现该报错。已重新发布 `publish/` 与 `publish-green/`（10-08 01:05）。

## PE 中文字体（10-08 06:15）

**现象**：PE 部署界面的中文全是方块（`□□□□ Windows □□`、`52% | □□□□□□□□□`），而 `Windows`、`52%`、`|` 正常。

**根因（两层）**：

1. 原生界面用 GDI 自绘文字，字体面名最初写的是 `Segoe UI`。WinPE 普遍**没有 FontLink 链接表**（正常 Windows 靠它把缺字回退到中文字体），而 Segoe UI 本身没有汉字字形，于是每个汉字都落到缺字形框。
2. 第一版修法是「随载荷放一份 CTPrep.Font.ttf，界面私有加载」，但 **`startnet.cmd` 的拷贝清单里没有这个文件**。界面是从 RAM 盘 `X:\ctprep\` 运行的（`startnet.cmd` 只把 `deploy.cmd / CTPrep.PeUi.exe / diskpart-target.txt / unattend.xml / SetupComplete.cmd / lang.txt` 拷过去），所以 `AddFontResourceExW` 必然失败，兜底探测在这个 PE 里也找不到雅黑/黑体，最终仍停在 Segoe UI —— 方块照旧。

**现在的修法（不再依赖任何文件拷贝）**：

1. 字体**直接链接进 `CTPrep.PeUi.exe`**：`tools/build-pe-font.py` 产出 `build/pe-font/font_data.c`（字体字节的 C 数组），`tools/build-pe-ui.ps1` 与 `main.c` 一起编译。界面启动时用 `AddFontMemResourceEx` 从内存注册，再按家族名 `CTPrep UI` 创建字体。`CTPrep.PeUi.exe` 因此从 22 KB 变成 **2.14 MB**，但 PE 侧少了一个可能丢失的文件。
2. 按钮标题与失败日志框也一并 `WM_SETFONT`，否则它们的汉字仍是方块。
3. 兜底顺序：内置字体 → 界面同级目录的 `CTPrep.Font.ttf`（可替换字体而无需重新编译）→ 该 PE 自带的 CJK 家族（`Microsoft YaHei UI` / `Microsoft YaHei` / `DengXian` / `SimHei` / `SimSun`）→ Segoe UI。每一档都用 `GetGlyphIndicesW` 实探能否画出汉字。
4. 界面会在 `X:\ctprep\ui-font.log`（UTF-8 带 BOM）写下最终生效的 **requested / realized / source / glyphs**。若再次出现方块，这份日志能直接说明是哪一档失效、GDI 实际替换成了哪个字体，不必再靠截图猜。
5. 字体来源：本机 `C:\Windows\Fonts\NotoSansSC-VF.ttf`（Noto Sans SC，**SIL OFL 1.1**，可自由内置与再分发）。可变字体默认实例是 **wght=100（Thin）**，直接嵌入会被 GDI 当细体，所以先用 `fontTools.varLib.instancer` 固定到 wght=400，再子集化到「PE 界面可能显示的全部字符」——`main.c` 里所有宽字符串 + 全部 GB2312 + ASCII + 常用标点，共 7550 个字符，产出 **2.11 MB**。家族名改成私有的 `CTPrep UI`，避免与 PE 里已装字体撞名。

**实测（主机，与界面完全相同的调用序列）**：

```
control Segoe UI              -> realized=Segoe UI   glyphs: FFFF FFFF FFFF FFFF FFFF FFFF   # 方块
AddFontMemResourceEx          -> OK, fonts in resource=2
after registration, ask "CTPrep UI"
                              -> realized=CTPrep UI  glyphs: 0D97 07BF 1949 1384 0BBD 03EB   # 真实字形
```

**注意**：字体必须是静态字体（`instancer` 已去掉 `fvar`/`gvar`），不要直接把 `NotoSansSC-VF.ttf` 原样嵌入。改动 `main.c` 里能显示的文字后建议重跑 `tools/build-pe-font.py` 与 `tools/build-pe-ui.ps1`（子集是按字符集裁的；不过它已覆盖全部 GB2312，通常无需重跑）。

## 高级设置新功能（10-08）

1. **可选安装磁盘**：安装方式区新增「目标磁盘」下拉框（默认「自动」= 当前系统盘）。选择非系统盘时：
   - 自动勾选并锁定「整盘重建」（该磁盘上没有可保留的系统文件），红色提示 + 确认窗口第 2 步列出具体磁盘；
   - PE 侧先 `diskpart` 清空目标磁盘并新建 ESP/MSR/主分区（UEFI）或 系统/主分区（BIOS），`bcdboot /s S:（或 B:）` 把引导写入新磁盘；
   - 部署收尾给旧系统盘的 ESP/系统保留分区临时分配盘符，删掉旧引导库里的 PE 一次性引导项与私有设备选项对象（`{{OLD_BOOT_CLEANUP}}`）；
   - 暂存分区永远不落在目标磁盘上（复用候选排除目标盘、压缩固定取系统卷）；跨盘时删除暂存分区后把空间还给**被压缩的宿主分区**，而不是目标卷。
2. **格式化引导分区开关**（默认开）：UEFI 格式化 ESP、BIOS 格式化系统保留分区，清掉旧系统的引导项；关闭则保留旧引导文件（安装中途失败时旧系统仍可引导）。整盘重建时该开关无意义（新分区必然是新格式化的），自动置灰。BIOS 且无系统保留分区、保留文件模式下，勾选它会清掉目标分区根目录的 `bootmgr`/`BOOTNXT`/`Boot`；BIOS 引导库位于目标分区时，PE 收尾清理路径也从 `W:\Boot\BCD` 生效。
3. **禁用 Windows Defender 开关**（默认关）：首次进入系统前（`SetupComplete.cmd` 第 4 步）写策略注册表（`DisableAntiSpyware`、`DisableRealtimeMonitoring` 等 8 项）并停用 `WinDefend`/`WdNisSvc`/`WdNisDrv`。较新版本的 Windows 可能因篡改防护自动恢复，属预期内，界面气泡已说明。
4. **时区改为下拉框**：由 `TimeZoneInfo.GetSystemTimeZones()` 生成（Windows 时区标识 + 系统本地化显示名，按 UTC 偏移排序、可输入关键字检索），取代原来的自由文本框；写进应答文件的仍是 Windows 时区 ID。

**验证状态**：以上渲染产物（diskpart 脚本、deploy.cmd、SetupComplete.cmd）用反射调用真实私有渲染方法逐组合断言（64 项全部通过，含跨盘/同盘、UEFI/BIOS、开关开/关、ASCII 纯净性与占位符残留检查）；`deploy.cmd` 中跨盘清理段的 batch 语义（`%%L` 转义、if/else 块内变量展开顺序）已人工复核。**尚未在虚拟机实测**跨盘安装、格式化引导分区与 Defender 禁用三项行为；建议分别在有快照的 UEFI/BIOS 虚拟机中验证一次。功能改动会随准备流程写入暂存区，**改后必须重新执行准备流程**，仅重启旧 PE 无效。

## 链接清单（Pages）与官网（10-08 · 晚）

**动机**：镜像/PE 的托管地址会变（换存储、链接失效），硬编码在配置里意味着每次都要改配置。现在支持「链接清单」：程序在下载前请求一个 `.json`（建议部署在 Cloudflare Pages 上），按本机版本挑出真正的链接。

- **程序侧**：`Models/LinkManifest.cs` + `Services/LinkManifestService.cs`；`DownloadService.FetchTextAsync`（30 秒超时、512 KB 上限、UTF-8 BOM 容错）。凡是路径以 `.json` 结尾的地址（`config.ini` 的 `[Image]`/`[PE]` 条目、高级设置的镜像地址、分析镜像）都会按清单处理；匹配规则与 config.ini 一致（精确 VersionKey → 家族前缀 → Default）；清单条目的 `sha256` 留空则跳过校验并记警告。
- **docs/**：`index.html`（官网落地页，纯静态、无外部依赖）、`images.json`（清单示例）、`_headers`（Cloudflare Pages 用；GitHub Pages 原样发布、不生效）、`README.md`（格式规范 + 部署步骤 + 维护说明）。
- **同日目录调整**：官网从 `web/` 移到 `docs/`（对应 GitHub Pages 的 `/docs` 发布方式）；`runtime/pe/boot.wim` 改为经 **Git LFS** 入库（`.gitattributes` 已跟踪，本库已 `git lfs install --local`；克隆需装 git-lfs，CI 的 checkout 已开启 `lfs: true`）。
- **验证**：smoke5 新增 18 项断言全过 —— IsManifestUrl / Parse / MatchImage（精确、家族、Default、非法 JSON）+ 本地 HTTP 服务器全链路（拉取、按 URL 缓存、BOM 前缀、超限拒绝）。编译 0 错 0 警，`publish-green/` 已重新发布。
- **仓库与官网**：远程仓库为 `github.com/com-in/CTPrep`（公开，官网链接已按其填写）；剩余一步：仓库 Settings → Pages 选择 `main` / `/docs` 发布官网。GitHub Actions 的 build 工作流在每次推送后自动运行。`.workbuddy/`（Agent 工作记忆）按用户决定不随公开仓库发布（已在 `.gitignore` 忽略）。

## 使用新版

将整个 `publish-green` 文件夹复制到**虚拟机内非 C 盘**，运行其中的 CTPrep.exe，重新执行准备流程，然后进入 PE。不要仅重启此前已准备的旧 PE；旧暂存区不会自动更新。

绿色版自带 .NET 8 Windows Desktop 运行时，不需要另行安装 .NET。PE 界面是独立的 x64 Win32 程序，不依赖 .NET、PowerShell、HTA 或浏览器。默认 PE 资源随发布目录一起提供；最新启动脚本在准备时注入暂存的 boot.wim，图形程序复制到暂存区，进入 PE 后从 X 盘运行。

## 行为

- 无边框全屏置顶，隐藏入口控制台，以无窗口子进程运行部署脚本。
- 安装期间屏蔽普通关闭、Alt+F4 和最小化；不能阻止强制终止进程或虚拟机断电。
- 显示部署阶段进度，并在应用映像期间读取 DISM 百分比；不是按时间伪造进度。
- 失败后保留界面，提供查看日志和返回维护入口。
- 准备时检查 PE 必要工具；部署前检查镜像索引与目标盘符占用；DISM 失败立即停止，避免继续执行 bcdboot。
- bcdboot 使用详细输出，保留真实退出码；失败提示不再保证旧系统仍可启动。

## 验证范围与日志

本次仅在项目目录编译与发布，未在主机执行安装、分区、BCD 修改、PE 启动或部署测试。虚拟机端到端验证仍待进行；旧截图中的 BCDBOOT_FAILED 根因不能仅凭截图确认。

BCD 修复同样只在项目内编译校验，未在主机运行 bcdedit（bcdedit 需要管理员权限，且主机没有对应暂存盘）。修复后仍需在虚拟机里验证：准备流程不再报 `{ramdiskoptions}` 错误、重启能进入 PE、部署完成后 BCD 中不残留临时引导项与私有设备选项对象。

仅在有快照的虚拟机内验证 BIOS/UEFI 安装、全屏显示、关闭保护与故障退出。失败日志为 `X:\ctprep\deploy.log`，并尽量保存至暂存目录 `deploy-failed.log`。X 盘日志需在重启前保存。

PE 界面还会在 `X:\ctprep\ui-font.log` 记录界面实际使用的字体（`requested` / `realized` / `source` / `glyphs`）。中文一旦再次出现方块，先看这份文件：`source=embedded...` 且 `glyphs` 无 `FFFF` 说明内置字体已生效；若 `source=NONE` 则说明内置字体在该 PE 里注册失败且该 PE 没有任何中文字体，`realized` 会显示 GDI 最终替换成了哪个字体。

## 从源码构建

1. `python tools/build-pe-font.py`（跑 PC 版 Python 即可，需要 `pip install fonttools`）。产物是两个**生成物、不入库**的文件：`build/pe-font/CTPrep.Font.ttf`（子集字体，供检查）与 `build/pe-font/font_data.c`（同字节的 C 数组，会被链接进界面）。只在字符集或字体需要重做时跑。
2. `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-pe-ui.ps1`（改了 `src/CtPrep.PeUi/main.c` 必须跑；它同时编译 `main.c` 与 `build/pe-font/font_data.c`，缺少字体产物会直接报错并提示先跑第 1 步）
3. `dotnet publish src/CtPrep.App/CtPrep.App.csproj -p:PublishProfile=Green-x64`

重建 `runtime\pe\boot.wim`（第 2 步的输入）走 `tools/slim-lightningpe.ps1`，它需要雷电PE 整包里的 `Sources\V1.8-B3.2_NVME.ISO`。仓库内原有的雷电PE 整包 `tools/Lightning PE V1.8-B3.2_NVME.7z`（1.5 GB）已在 10-08 清理时删除，**重建 PE 前需自备该整包**；`boot.wim`（经 Git LFS 入库）与 `boot.sdi` 本身已保留在 `runtime\pe`。注意第 2 步会在项目内重建 `build\zig-cache`（约 500 MB 的 Zig 编译缓存），不需要时可再删。

原生编译器使用项目内的便携 Zig 0.14.1，默认路径 `tools/compiler/ziglang/zig.exe`，也可通过脚本 `-Zig` 参数指定；未全局安装。主程序把原生界面 EXE 嵌入资源，而**界面字体已经链接在这个 EXE 内部**（`AddFontMemResourceEx` 从内存注册），所以主程序不再单独嵌入字体文件，PE 侧也不再有「字体文件必须被拷到某个目录」这个前置条件。
