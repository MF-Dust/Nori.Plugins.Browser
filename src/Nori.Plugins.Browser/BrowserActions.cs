using System.Text.Json.Nodes;
using Nori.PluginRuntime;

namespace Nori.Plugins.Browser;

/// <summary>读取浏览器会话状态（只读）。</summary>
internal sealed class BrowserStatusAction(BrowserController controller) : IPluginActionContribution
{
	public string Id => "status";

	public string Description => "查询隔离 Edge 浏览器会话状态：是否可用、是否已启动、是否有任务在执行。";

	public JsonNode? ParametersSchema => new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

	public PluginActionPermission Permission => PluginActionPermission.Safe;

	public Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken) =>
		Task.FromResult<JsonObject?>(controller.GetStatus().ToJson());
}

/// <summary>关闭浏览器会话并删除临时 profile（清理操作）。</summary>
internal sealed class BrowserCloseAction(BrowserController controller) : IPluginActionContribution
{
	public string Id => "close";

	public string Description => "取消正在执行的浏览器任务，关闭隔离 Edge 窗口并删除临时浏览数据。";

	public JsonNode? ParametersSchema => new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

	public PluginActionPermission Permission => PluginActionPermission.Safe;

	public async Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken) =>
		(await controller.StopAsync().ConfigureAwait(false)).ToJson();
}

/// <summary>
/// 执行结构化浏览器任务。不允许输入的版本需要用户确认；允许填写的版本按危险操作逐次确认。
/// </summary>
internal sealed class BrowserRunAction(BrowserController controller, bool allowInput) : IPluginActionContribution
{
	public string Id => allowInput ? "run_with_input" : "run";

	public string Description => allowInput
		? "在隔离 Edge 中按顺序执行浏览器动作，允许向唯一可见的表单元素填写文本 (fill)。遇到登录、支付、验证码、文件、下载或权限交互会停止并交给用户。最后一个 read_visible_text 的结果会返回。"
		: "在隔离 Edge 中按顺序执行浏览器动作：navigate(url)、click(selector)、scroll(pixels)、wait(milliseconds)、read_visible_text。不能填写表单；遇到登录、支付、验证码、文件、下载或权限交互会停止并交给用户。最后一个 read_visible_text 的结果会返回。";

	public JsonNode? ParametersSchema => BuildSchema(allowInput);

	public PluginActionPermission Permission => allowInput ? PluginActionPermission.Dangerous : PluginActionPermission.Confirm;

	public async Task<JsonObject?> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken)
	{
		BrowserTaskPlan plan;
		try
		{
			plan = BrowserTaskPlan.Parse(arguments);
		}
		catch (BrowserPlanException exception)
		{
			return new BrowserRunResult(false, 0, 0, null, "invalid_action", exception.Message).ToJson();
		}
		BrowserRunResult result = await controller.RunAsync(plan, allowInput, cancellationToken).ConfigureAwait(false);
		return result.ToJson();
	}

	internal static JsonObject BuildSchema(bool allowInput)
	{
		JsonArray types = ["navigate", "click", "scroll", "wait", "read_visible_text"];
		if (allowInput) types.Add("fill");
		JsonObject properties = new()
		{
			["type"] = new JsonObject { ["type"] = "string", ["enum"] = types },
			["url"] = new JsonObject { ["type"] = "string", ["description"] = "navigate 的 http/https 地址" },
			["selector"] = new JsonObject { ["type"] = "string", ["description"] = $"click{(allowInput ? "/fill" : string.Empty)} 的 CSS 选择器，必须恰好匹配一个可见元素" },
			["pixels"] = new JsonObject { ["type"] = "integer", ["description"] = $"scroll 的垂直像素，非零且绝对值不超过 {BrowserSafetyPolicy.MaxScrollPixels}" },
			["milliseconds"] = new JsonObject { ["type"] = "integer", ["description"] = $"wait 的毫秒数，1 到 {BrowserSafetyPolicy.MaxWaitMilliseconds}" },
		};
		if (allowInput)
			properties["text"] = new JsonObject { ["type"] = "string", ["description"] = $"fill 写入的文本，最多 {BrowserSafetyPolicy.MaxInputCharacters} 字符" };
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["actions"] = new JsonObject
				{
					["type"] = "array",
					["minItems"] = 1,
					["maxItems"] = BrowserTaskLimits.MaxActions,
					["items"] = new JsonObject
					{
						["type"] = "object",
						["properties"] = properties,
						["required"] = new JsonArray("type"),
					},
				},
			},
			["required"] = new JsonArray("actions"),
		};
	}
}
