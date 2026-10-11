using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nori.Plugins.Browser;

/// <summary>浏览器 DOM 自动化允许的结构化动作种类。</summary>
public enum BrowserActionKind
{
	/// <summary>导航到受限 HTTP/HTTPS 地址。</summary>
	Navigate,
	/// <summary>点击唯一可见元素。</summary>
	Click,
	/// <summary>填写唯一可见表单元素；只有显式允许输入的入口可以执行。</summary>
	Fill,
	/// <summary>在当前页面滚动有限距离。</summary>
	Scroll,
	/// <summary>等待有限时间。</summary>
	Wait,
	/// <summary>读取受大小限制的可见文本。</summary>
	ReadVisibleText,
}

/// <summary>浏览器结构化动作解析或策略校验错误；消息可直接展示给用户。</summary>
public sealed class BrowserPlanException : FormatException
{
	/// <summary>创建解析错误。</summary>
	public BrowserPlanException(string message) : base(message) { }
}

/// <summary>浏览器 DOM 自动化动作基类；不包含脚本、文件或权限提升能力。</summary>
public abstract record BrowserAction
{
	/// <summary>动作种类。</summary>
	public abstract BrowserActionKind Kind { get; }
}

/// <summary>导航动作。</summary>
public sealed record BrowserNavigateAction(string Url) : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.Navigate;
}

/// <summary>元素点击动作。</summary>
public sealed record BrowserClickAction(string Selector) : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.Click;
}

/// <summary>表单填写动作；文本只在内存执行链中传递。</summary>
public sealed record BrowserFillAction(string Selector, string Text) : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.Fill;
}

/// <summary>页面滚动动作。</summary>
public sealed record BrowserScrollAction(int Pixels) : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.Scroll;
}

/// <summary>有限等待动作。</summary>
public sealed record BrowserWaitAction(int Milliseconds) : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.Wait;
}

/// <summary>读取页面可见文本的动作。</summary>
public sealed record BrowserReadVisibleTextAction : BrowserAction
{
	/// <inheritdoc />
	public override BrowserActionKind Kind => BrowserActionKind.ReadVisibleText;
}

/// <summary>浏览器任务的不可放宽边界。</summary>
public static class BrowserTaskLimits
{
	/// <summary>单个任务最多动作数。</summary>
	public const int MaxActions = 20;

	/// <summary>可见文本结果的最大 UTF-8 字节数。</summary>
	public const int MaxVisibleTextBytes = 32 * 1024;

	/// <summary>单个任务最多持续时间。</summary>
	public static TimeSpan MaximumDuration { get; } = TimeSpan.FromSeconds(120);

	/// <summary>按 UTF-8 字节边界截断文本，绝不拆开 UTF-16 代理项对。</summary>
	public static string TruncateVisibleText(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (Encoding.UTF8.GetByteCount(value) <= MaxVisibleTextBytes) return value;

		byte[] buffer = new byte[MaxVisibleTextBytes];
		Encoder encoder = Encoding.UTF8.GetEncoder();
		encoder.Convert(value.AsSpan(), buffer.AsSpan(), flush: true, out int charactersUsed, out _, out _);
		if (charactersUsed > 0 && char.IsHighSurrogate(value[charactersUsed - 1])) charactersUsed--;
		return value[..charactersUsed];
	}
}

/// <summary>已解析并通过策略校验的浏览器动作计划；只保留当前内存执行所需数据。</summary>
public sealed class BrowserTaskPlan
{
	private readonly BrowserAction[] _actions;

	/// <summary>创建受动作数与安全策略限制的计划。</summary>
	public BrowserTaskPlan(IEnumerable<BrowserAction> actions)
	{
		ArgumentNullException.ThrowIfNull(actions);
		_actions = actions.ToArray();
		if (_actions.Length is < 1 or > BrowserTaskLimits.MaxActions)
			throw new BrowserPlanException($"浏览器任务动作数必须在 1 到 {BrowserTaskLimits.MaxActions} 之间");
		if (_actions.Any(action => action is null)) throw new BrowserPlanException("浏览器任务包含空动作");
		foreach (BrowserAction action in _actions) BrowserSafetyPolicy.ValidateAction(action);
	}

	/// <summary>动作列表。</summary>
	public IReadOnlyList<BrowserAction> Actions => _actions;

	/// <summary>计划是否包含需要更高授权的填写动作。</summary>
	public bool ContainsInput => _actions.Any(action => action.Kind == BrowserActionKind.Fill);

	/// <summary>从 JSON 文本解析严格白名单动作数组。</summary>
	public static BrowserTaskPlan Parse(string json)
	{
		JsonNode? node;
		try { node = JsonNode.Parse(json); }
		catch (JsonException) { throw new BrowserPlanException("浏览器任务不是有效的 JSON"); }
		return Parse(node);
	}

	/// <summary>从动作数组或 {"actions":[...]} 对象解析严格白名单动作。</summary>
	public static BrowserTaskPlan Parse(JsonNode? value)
	{
		if (value is JsonObject wrapper) value = wrapper["actions"];
		if (value is not JsonArray array) throw new BrowserPlanException("浏览器任务 actions 必须是数组");
		if (array.Count > BrowserTaskLimits.MaxActions)
			throw new BrowserPlanException($"浏览器任务最多允许 {BrowserTaskLimits.MaxActions} 个动作");
		return new BrowserTaskPlan(array.Select(ParseAction));
	}

	private static BrowserAction ParseAction(JsonNode? value)
	{
		if (value is not JsonObject item) throw new BrowserPlanException("浏览器动作必须是对象");
		string type = RequiredString(item, "type");
		return type switch
		{
			"navigate" => new BrowserNavigateAction(RequiredString(item, "url")),
			"click" => new BrowserClickAction(RequiredString(item, "selector")),
			"fill" => new BrowserFillAction(RequiredString(item, "selector"), RequiredString(item, "text")),
			"scroll" => new BrowserScrollAction(RequiredInt(item, "pixels")),
			"wait" => new BrowserWaitAction(RequiredInt(item, "milliseconds")),
			"read_visible_text" or "read-visible-text" => new BrowserReadVisibleTextAction(),
			_ => throw new BrowserPlanException("浏览器动作类型不在白名单内"),
		};
	}

	private static string RequiredString(JsonObject item, string name)
	{
		if (item[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
			throw new BrowserPlanException($"浏览器动作缺少字符串字段: {name}");
		return value.GetValue<string>();
	}

	private static int RequiredInt(JsonObject item, string name)
	{
		if (item[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
			|| !value.TryGetValue(out int number) && !TryGetIntegralDouble(value, out number))
			throw new BrowserPlanException($"浏览器动作缺少整数字段: {name}");
		return number;
	}

	private static bool TryGetIntegralDouble(JsonValue value, out int number)
	{
		number = 0;
		if (!value.TryGetValue(out double raw) || raw != Math.Floor(raw) || raw is < int.MinValue or > int.MaxValue) return false;
		number = (int)raw;
		return true;
	}
}
