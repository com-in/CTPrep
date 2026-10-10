# Universal CRT（app-local 部署，给 Windows 7 用）

这里的 43 个文件是 Microsoft 的 Universal C Runtime（UCRT）：`ucrtbase.dll` 加上
15 个 `api-ms-win-crt-*.dll` 和 27 个 `api-ms-win-core-*.dll` 转发器。

## 为什么放在这里

Windows 7 不带 UCRT。缺了它，任何依赖它的进程在**加载阶段**就会失败：

```
无法启动此程序，因为计算机中丢失 api-ms-win-crt-runtime-l1-1-0.dll
```

这个错误发生在程序自己的代码跑起来之前，所以在程序里做检测或提示都没用——
只能让它别缺。

Windows 8 及以上自带（系统用 API set 解析到 `ucrtbase.dll`），但 Windows 7 需要先装
KB2999226 才会有。本目录是把这个前置条件去掉：按 Microsoft 文档的
[app-local 部署](https://learn.microsoft.com/cpp/windows/universal-crt-deployment)
把整套文件放到 exe 同目录，Windows 7 上就能直接跑。

`CtPrep.App.csproj` 会把这里的文件复制到输出目录与发布目录的**根**（和 `CTPrep.exe` 同级）。

## 在 Windows 10 / 11 上会不会有影响

不会。这些名字在 Win10/11 的 API set schema 里有映射，加载器会直接解析到系统自带的
`ucrtbase.dll`，程序目录里的这些副本不会被使用。

## 版本与来源

- 文件版本：`10.0.22000.194`（全套一致）
- 来源：`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36\`
  —— .NET 6 的运行时自带这一整套，因为 .NET 6 还支持 Windows 7 SP1；
  .NET 8 放弃了 Windows 7，运行时里已经不再包含。
- 许可：UCRT 属于 Microsoft 的**可再发行组件**（Distributable Code），
  允许随应用程序一起分发。调试版（`ucrtbased.dll` 等）不可再发行，这里没有。

## 如果还是起不来

UCRT 的 app-local 部署是受支持但「不推荐」的方式。若这台 Win7 仍然报缺失，
按 Microsoft 的首选做法装一次官方更新即可（装完可以把本目录删掉）：

- Windows 7 SP1 的 UCRT 更新：**KB2999226**（后续修复版 KB3118401）
- 或者装 **Visual C++ 2015-2022 可再发行组件包（x64）**，它会把 UCRT 一起装上
