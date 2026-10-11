using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace Nori.Plugins.Browser.Tests;

/// <summary>浏览器结构化动作解析与安全策略测试。</summary>
[SuppressMessage("Security", "S2068", Justification = "带伪凭据的 URL 用于验证浏览器自动化策略拒绝行为。")]
public sealed class BrowserTaskPlanTests
{
	[Fact]
	public void 只解析六种白名单动作且支持actions包装对象()
	{
		const string json = """
			{"actions":[
				{"type":"navigate","url":"https://example.test"},
				{"type":"click","selector":"#open"},
				{"type":"fill","selector":"#query","text":"只留在内存"},
				{"type":"scroll","pixels":120},
				{"type":"wait","milliseconds":50},
				{"type":"read_visible_text"}
			]}
			""";

		BrowserTaskPlan plan = BrowserTaskPlan.Parse(json);

		Assert.Equal(6, plan.Actions.Count);
		Assert.IsType<BrowserNavigateAction>(plan.Actions[0]);
		Assert.IsType<BrowserClickAction>(plan.Actions[1]);
		Assert.IsType<BrowserFillAction>(plan.Actions[2]);
		Assert.IsType<BrowserScrollAction>(plan.Actions[3]);
		Assert.IsType<BrowserWaitAction>(plan.Actions[4]);
		Assert.IsType<BrowserReadVisibleTextAction>(plan.Actions[5]);
		Assert.True(plan.ContainsInput);
		Assert.Equal(TimeSpan.FromSeconds(120), BrowserTaskLimits.MaximumDuration);
	}

	[Theory]
	[InlineData("eval")]
	[InlineData("download")]
	[InlineData("upload")]
	[InlineData("password")]
	[InlineData("permission_bypass")]
	public void 拒绝非白名单动作且不回显类型(string type)
	{
		BrowserPlanException exception = Assert.Throws<BrowserPlanException>(() =>
			BrowserTaskPlan.Parse(new JsonArray(new JsonObject { ["type"] = type })));
		Assert.DoesNotContain(type, exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("{}")]
	[InlineData("[]")]
	[InlineData("[1]")]
	[InlineData("[{\"type\":\"click\"}]")]
	[InlineData("[{\"type\":\"scroll\",\"pixels\":\"10\"}]")]
	[InlineData("[{\"type\":\"wait\",\"milliseconds\":1.5}]")]
	public void 拒绝格式错误的计划(string json)
	{
		Assert.Throws<BrowserPlanException>(() => BrowserTaskPlan.Parse(json));
	}

	[Fact]
	public void 整数字段接受整值浮点()
	{
		BrowserTaskPlan plan = BrowserTaskPlan.Parse("[{\"type\":\"wait\",\"milliseconds\":10.0}]");
		Assert.Equal(10, Assert.IsType<BrowserWaitAction>(plan.Actions[0]).Milliseconds);
	}

	[Fact]
	public void 拒绝超过二十个动作()
	{
		JsonArray actions = [];
		for (int index = 0; index <= BrowserTaskLimits.MaxActions; index++) actions.Add(new JsonObject { ["type"] = "read_visible_text" });
		Assert.Throws<BrowserPlanException>(() => BrowserTaskPlan.Parse(actions));
	}

	[Theory]
	[InlineData("https://example.com")]
	[InlineData("http://localhost:8080/test")]
	public void 只允许http地址(string value)
	{
		Uri uri = BrowserSafetyPolicy.ValidateNavigation(value);
		Assert.Equal(new Uri(value).Host, uri.Host);
	}

	[Theory]
	[InlineData("file:///C:/secret.txt")]
	[InlineData("javascript:alert(1)")]
	[InlineData("https://user:password@example.com")]
	[InlineData("")]
	public void 拒绝危险或无效地址(string value)
	{
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateNavigation(value));
		Assert.Throws<BrowserPlanException>(() => new BrowserTaskPlan([new BrowserNavigateAction(value)]));
	}

	[Fact]
	public void 操作参数有边界()
	{
		Assert.Equal(200, BrowserSafetyPolicy.ValidateScroll(200));
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateScroll(0));
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateScroll(BrowserSafetyPolicy.MaxScrollPixels + 1));
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateWait(BrowserSafetyPolicy.MaxWaitMilliseconds + 1));
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateSelector("#a\n#b"));
		Assert.Throws<BrowserPlanException>(() => BrowserSafetyPolicy.ValidateInput(new string('x', BrowserSafetyPolicy.MaxInputCharacters + 1)));
	}

	[Fact]
	public void 可见文本按三十二KiB字节边界截断()
	{
		string source = string.Concat(Enumerable.Repeat("海", BrowserTaskLimits.MaxVisibleTextBytes));
		string bounded = BrowserTaskLimits.TruncateVisibleText(source);

		Assert.True(Encoding.UTF8.GetByteCount(bounded) <= BrowserTaskLimits.MaxVisibleTextBytes);
		Assert.True(bounded.Length < source.Length);
		Assert.False(bounded.Length > 0 && char.IsHighSurrogate(bounded[^1]));
	}
}
