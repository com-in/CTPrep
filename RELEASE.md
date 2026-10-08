# 发布流程

仓库里有两个工作流，职责分开：

| 工作流 | 触发 | 做什么 |
| --- | --- | --- |
| `build.yml` | 每次 push / PR | 编译并上传一份 7 天有效期的构建产物。**不发布。** |
| `release.yml` | 推送形如 `v1.2.3` 的 tag，或手动指定版本 | 校验 → 构建两个版本 → 创建 GitHub Release |

**只有 `release.yml` 会发布。** 普通提交、合并 PR、同步机器人（`sync-links`）的提交都不会触发发布。

## 发布前必须满足的三个条件

1. **版本号**：tag 形如 `v1.2.3`，且与 `src/CtPrep.App/CtPrep.App.csproj` 里的 `<Version>` **完全一致**；
2. **镜像清单可用**：`docs/images.json` 里至少有一个真实的镜像地址（`http(s)` 开头，且不是 `example.com` 之类的占位）；
3. **tag 尚未发布过**：同名 Release 已存在时直接失败，不会覆盖。

任一条件不满足，工作流会在发布之前停下，不会留下半成品 Release。

## 发布步骤

```bash
# 1. 改版本号（与要打的 tag 保持一致）
#    src/CtPrep.App/CtPrep.App.csproj  ->  <Version>1.2.0</Version>

# 2. 确认镜像清单里是真实地址，不是占位
#    docs/images.json

# 3. 提交
git add src/CtPrep.App/CtPrep.App.csproj docs/images.json
git commit -m "Release 1.2.0"
git push origin main

# 4. 在同一个提交上打 tag 并推送 —— 这一步才是真正的发布动作
git tag v1.2.0
git push origin v1.2.0
```

推送 tag 后 `release.yml` 自动运行，产出：

- `CTPrep-v1.2.0-green.zip` —— 绿色版，自带 .NET 8 运行时
- `CTPrep-v1.2.0-lite.zip` —— 轻量版，需目标机器已装 .NET 8 桌面运行时

Release 说明里会写明**镜像清单的获取地址**（自定义域名优先，否则回退 `github.io`），格式如下：

```
https://ctprep.acmcdev.top/images.json
```

## 手动发布

Actions 页面 → `release` → **Run workflow** → 填写版本号（写 `1.2.0` 或 `v1.2.0` 都可以）。同样要满足上面三个条件。

## 版本号约定

`主版本.次版本.修订号`：

- **修订号** —— 修 bug、脚本与文案调整；
- **次版本** —— 新增功能、新增选项；
- **主版本** —— 不兼容的配置变更，或部署流程改动（这类改动要求用户重走准备流程）。

## 发布后

- 官网（`docs/`，GitHub Pages）会随 push 自动更新；
- 需要回滚时用 `gh release delete <tag>`，并保留对应的构建产物以备排查。
