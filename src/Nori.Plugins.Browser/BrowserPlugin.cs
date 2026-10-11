using Nori.PluginRuntime;

namespace Nori.Plugins.Browser;

/// <summary>
/// Nori 浏览器自动化插件入口。
///
/// 注册四个伴侣工具（status / close / run / run_with_input）与一个可选的原生控制页面。
/// 停用时关闭 Edge 会话并删除临时 profile。
/// </summary>
public sealed class BrowserPlugin : INoriPlugin
{
	private readonly Func<IPluginLogger, BrowserController> _controllerFactory;
	private readonly List<IPluginRegistration> _registrations = [];
	private BrowserController? _controller;

	/// <summary>宿主通过无参构造创建插件。</summary>
	public BrowserPlugin()
		: this(logger => new BrowserController(new EdgeBrowserSessionFactory(), logger))
	{
	}

	internal BrowserPlugin(Func<IPluginLogger, BrowserController> controllerFactory)
	{
		_controllerFactory = controllerFactory;
	}

	public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		BrowserController controller = _controllerFactory(context.Logger);
		_controller = controller;
		_registrations.Add(context.Contributions.Register<IPluginActionContribution>(new BrowserStatusAction(controller)));
		_registrations.Add(context.Contributions.Register<IPluginActionContribution>(new BrowserCloseAction(controller)));
		_registrations.Add(context.Contributions.Register<IPluginActionContribution>(new BrowserRunAction(controller, allowInput: false)));
		_registrations.Add(context.Contributions.Register<IPluginActionContribution>(new BrowserRunAction(controller, allowInput: true)));
		if (context.Capabilities.Statuses.Any(status => status.Id == PluginCapabilityIds.AvaloniaUi && status.Available))
			_registrations.Add(context.Contributions.Register<IPluginPageContribution>(new BrowserPageContribution(controller)));
		return ValueTask.CompletedTask;
	}

	public async ValueTask DeactivateAsync(CancellationToken cancellationToken)
	{
		foreach (IPluginRegistration registration in _registrations) registration.Dispose();
		_registrations.Clear();
		BrowserController? controller = Interlocked.Exchange(ref _controller, null);
		if (controller is not null) await controller.DisposeAsync().ConfigureAwait(false);
	}
}
