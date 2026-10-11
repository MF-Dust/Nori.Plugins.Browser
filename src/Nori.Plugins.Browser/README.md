# Nori 浏览器自动化 (io.nori.browser)

在独立的临时 Microsoft Edge 会话中执行受限的浏览器动作。仅支持 Windows，需要已安装 Edge 稳定版；插件不会下载浏览器，也不会使用你的默认 Edge 配置。

## 伴侣工具

| 工具 | 权限 | 说明 |
| --- | --- | --- |
| `status` | safe | 查询会话状态 |
| `close` | safe | 取消任务、关闭 Edge 并删除临时浏览数据 |
| `run` | confirm | 执行 navigate / click / scroll / wait / read_visible_text |
| `run_with_input` | dangerous | 在 `run` 基础上允许 fill 填写表单 |

每个任务最多 20 个动作、总时长 120 秒；只允许不含凭据的 http/https 地址，选择器必须恰好命中一个可见元素。遇到登录、支付、验证码、文件选择、下载、对话框或权限交互时立即停止，交由用户在 Edge 窗口中处理。

## 控制页面

在 **设置 → 插件** 中打开“浏览器自动化”页面，可手动启动/关闭 Edge 并执行 JSON 动作计划。
