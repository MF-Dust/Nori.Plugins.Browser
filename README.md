# Nori.Plugins.Browser

Nori Desktop 的浏览器自动化插件（`io.nori.browser`）。在独立的临时 Microsoft Edge 会话中执行受限的导航、点击、滚动、等待、读取可见文本与填写动作。

- 仅支持 Windows x64，需要已安装 Microsoft Edge 稳定版；插件不会下载浏览器，也不会使用默认 Edge 配置。
- Playwright 1.56 与其 Node 驱动只随插件包分发，Nori Desktop 主程序不再携带。
- 需要 Nori Desktop Plugin API **2.3**（[`Nori.PluginSDK`](https://www.nuget.org/packages/Nori.PluginSDK/) 2.3.0）。

## 伴侣工具

插件激活后，宿主把以下动作注册为 AI 工具 `plugin__io_nori_browser__<id>`：

| 工具 | 权限 | 说明 |
| --- | --- | --- |
| `status` | safe | 查询会话状态 |
| `close` | safe | 取消任务、关闭 Edge 并删除临时浏览数据 |
| `run` | confirm | 执行 navigate / click / scroll / wait / read_visible_text |
| `run_with_input` | dangerous | 在 `run` 基础上允许 fill 填写表单 |

参数示例：

```json
{"actions":[{"type":"navigate","url":"https://example.com"},{"type":"read_visible_text"}]}
```

安全边界：

- 每个任务最多 20 个动作、总时长 120 秒，同一时间只执行一个任务。
- 只允许不含凭据的 http/https 地址；选择器必须恰好命中一个可见元素；滚动 ≤ 2000 像素，等待 ≤ 30 秒，填写文本 ≤ 4096 字符。
- 页面出现登录、支付、验证码、文件选择、下载、对话框或权限交互时立即停止，交由用户在 Edge 窗口中处理。
- 可见文本最多返回 32 KiB；日志只记录生命周期与错误码，不记录地址、选择器或文本。

## 控制页面

在 Nori 的 **设置 → 插件** 中打开“浏览器自动化”页面，可手动启动/关闭 Edge 并执行 JSON 动作计划。

## 构建

需要 .NET 10 SDK。

```bash
dotnet build Nori.Plugins.Browser.slnx -c Release
dotnet test Nori.Plugins.Browser.slnx -c Release --no-build
dotnet build src/Nori.Plugins.Browser -c Release -t:PackNoriPlugin
```

插件包输出到 `artifacts/io.nori.browser.noripack`（约 35 MiB，含 win32_x64 Node 驱动）。在 Nori 的 **设置 → 插件 → 安装本地插件** 中选择该文件，确认后启用。

包内布局：

```text
manifest.json
README.md
LICENSE
lib/Nori.Plugins.Browser.dll
lib/Microsoft.Playwright.dll
lib/Microsoft.Bcl.AsyncInterfaces.dll
lib/.playwright/            Playwright Node 驱动（Playwright 从 Microsoft.Playwright.dll 所在目录查找）
```

`Nori.PluginRuntime.dll` 与 Avalonia 由宿主提供，不能放进插件包。

## 发布

推送 `v<版本>` 标签后，Release 工作流会构建、测试并把 `.noripack` 附加到 GitHub Release。标签版本必须与 `manifest.json` 的 `version` 一致。

## 许可证

[GPL-3.0](LICENSE)。Microsoft Playwright 使用 MIT 许可证。
