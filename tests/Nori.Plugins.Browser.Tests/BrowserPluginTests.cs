using System.Text.Json.Nodes;
using Nori.PluginRuntime;

namespace Nori.Plugins.Browser.Tests;

/// <summary>插件入口、manifest 与工具权限测试。</summary>
public sealed class BrowserPluginTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task 激活注册四个工具并按能力注册页面(bool avaloniaAvailable)
	{
		FakeBrowserSessionFactory factory = new();
		BrowserPlugin plugin = new(logger => new BrowserController(factory, logger));
		FakeContext context = new(avaloniaAvailable);

		await plugin.ActivateAsync(context, CancellationToken.None);

		Dictionary<string, PluginActionPermission> actions = context.Registered.OfType<IPluginActionContribution>()
			.ToDictionary(action => action.Id, action => action.Permission);
		Assert.Equal(PluginActionPermission.Safe, actions["status"]);
		Assert.Equal(PluginActionPermission.Safe, actions["close"]);
		Assert.Equal(PluginActionPermission.Confirm, actions["run"]);
		Assert.Equal(PluginActionPermission.Dangerous, actions["run_with_input"]);
		Assert.Equal(avaloniaAvailable, context.Registered.OfType<IPluginPageContribution>().Any());

		IPluginActionContribution run = context.Registered.OfType<IPluginActionContribution>().Single(action => action.Id == "run");
		await run.InvokeAsync(new JsonObject { ["actions"] = new JsonArray(new JsonObject { ["type"] = "read_visible_text" }) }, CancellationToken.None);
		Assert.Single(factory.Created);

		await plugin.DeactivateAsync(CancellationToken.None);
		Assert.All(context.Handles, handle => Assert.True(handle.Disposed));
		Assert.True(factory.Created[0].Disposed);
	}

	[Fact]
	public async Task 运行工具的参数模式与失败结果()
	{
		FakeBrowserSessionFactory factory = new();
		await using BrowserController controller = new(factory);
		BrowserRunAction run = new(controller, allowInput: false);
		BrowserRunAction runWithInput = new(controller, allowInput: true);

		JsonArray runTypes = run.ParametersSchema!["properties"]!["actions"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray();
		Assert.DoesNotContain(runTypes, node => node!.GetValue<string>() == "fill");
		JsonArray inputTypes = runWithInput.ParametersSchema!["properties"]!["actions"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray();
		Assert.Contains(inputTypes, node => node!.GetValue<string>() == "fill");

		JsonObject invalid = (await run.InvokeAsync(new JsonObject(), CancellationToken.None))!;
		Assert.False(invalid["ok"]!.GetValue<bool>());
		Assert.Equal("invalid_action", invalid["failureCode"]!.GetValue<string>());

		JsonObject fill = (await run.InvokeAsync(new JsonObject
		{
			["actions"] = new JsonArray(new JsonObject { ["type"] = "fill", ["selector"] = "#q", ["text"] = "x" }),
		}, CancellationToken.None))!;
		Assert.Equal("input_not_allowed", fill["failureCode"]!.GetValue<string>());
		Assert.Equal(0, factory.Starts);
	}

	[Fact]
	public void manifest与入口类型一致()
	{
		string path = Path.Combine(Path.GetDirectoryName(typeof(BrowserPlugin).Assembly.Location)!, "manifest.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		Assert.Equal("io.nori.browser", manifest["id"]!.GetValue<string>());
		// 动作权限级别属于 Plugin API 2.3；更低版本宿主会把全部动作当作 safe，必须在加载前拒绝。
		Assert.Equal("2.3", manifest["apiVersion"]!.GetValue<string>());
		Assert.Equal("lib/Nori.Plugins.Browser.dll", manifest["runtime"]!["assembly"]!.GetValue<string>());
		Assert.Equal(["windows"], manifest["platforms"]!.AsArray().Select(node => node!.GetValue<string>()));
		Assert.Equal([PluginCapabilityIds.AvaloniaUi], manifest["optionalCapabilities"]!.AsArray().Select(node => node!.GetValue<string>()));

		Type entry = typeof(BrowserPlugin).Assembly.GetType(manifest["runtime"]!["entryType"]!.GetValue<string>(), throwOnError: true)!;
		Assert.True(typeof(INoriPlugin).IsAssignableFrom(entry));
		Assert.NotNull(entry.GetConstructor(Type.EmptyTypes));
	}

	private sealed class FakeContext(bool avaloniaAvailable) : IPluginContext, IContributionRegistry, IPluginCapabilities
	{
		public List<IPluginContribution> Registered { get; } = [];
		public List<Handle> Handles { get; } = [];

		public PluginDescriptor Plugin { get; } = new()
		{
			Id = "io.nori.browser", Name = "浏览器", Description = "测试", Version = "1.0.0", ApiVersion = "2.2",
		};
		public IPluginLogger Logger { get; } = new NullLogger();
		public IPluginStorage Storage => throw new NotSupportedException();
		public IPluginAssets Assets => throw new NotSupportedException();
		public IContributionRegistry Contributions => this;
		public IPluginCapabilities Capabilities => this;
		public CancellationToken StoppingToken => CancellationToken.None;

		public IReadOnlyList<PluginCapabilityStatus> Statuses =>
			[new PluginCapabilityStatus(PluginCapabilityIds.AvaloniaUi, true, avaloniaAvailable, avaloniaAvailable)];

		public IPluginRegistration Register<T>(T contribution) where T : class, IPluginContribution
		{
			Registered.Add(contribution);
			Handle handle = new();
			Handles.Add(handle);
			return handle;
		}

		public bool TryGet<T>(out T? capability) where T : class, IPluginCapability
		{
			capability = null;
			return false;
		}

		public T GetRequired<T>() where T : class, IPluginCapability => throw new NotSupportedException();
	}

	private sealed class Handle : IPluginRegistration
	{
		public bool Disposed { get; private set; }
		public void Dispose() => Disposed = true;
	}

	private sealed class NullLogger : IPluginLogger
	{
		public void Debug(string message) { }
		public void Info(string message) { }
		public void Warn(string message) { }
		public void Error(string message, Exception? exception = null) { }
	}
}
