using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nori.PluginRuntime;

namespace Nori.Plugins.Browser;

/// <summary>浏览器自动化手动控制页面的惰性工厂。</summary>
internal sealed class BrowserPageContribution(BrowserController controller) : IPluginPageContribution
{
	public string Id => "control";

	public string Title => "浏览器自动化";

	public IPluginPage CreatePage() => new BrowserPage(controller);
}

/// <summary>
/// 手动控制页面：查看状态、启动/关闭 Edge、执行用户编写的 JSON 动作计划。
/// 控制器回调可能来自任意线程，界面更新统一投递到 UI 线程。
/// </summary>
internal sealed class BrowserPage : IPluginPage
{
	private const string DefaultPlan = """
		[
		  { "type": "navigate", "url": "https://example.com" },
		  { "type": "read_visible_text" }
		]
		""";

	private static readonly JsonSerializerOptions ResultJsonOptions = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private readonly BrowserController _controller;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
	private readonly Button _start = new() { Content = "启动 Edge" };
	private readonly Button _stop = new() { Content = "关闭 Edge" };
	private readonly Button _run = new() { Content = "执行任务" };
	private readonly CheckBox _allowInput = new() { Content = "允许填写动作 (fill)" };
	private readonly TextBox _plan = new()
	{
		Text = DefaultPlan,
		AcceptsReturn = true,
		TextWrapping = TextWrapping.NoWrap,
		MinHeight = 160,
		FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
	};
	private readonly TextBox _result = new()
	{
		IsReadOnly = true,
		AcceptsReturn = true,
		TextWrapping = TextWrapping.Wrap,
		MinHeight = 120,
		PlaceholderText = "任务结果",
	};
	private readonly ScrollViewer _root;
	private bool _busy;
	private int _disposed;

	public BrowserPage(BrowserController controller)
	{
		_controller = controller;
		_start.Click += async (_, _) => await RunUiOperationAsync(token => _controller.StartAsync(token)).ConfigureAwait(true);
		_stop.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
		_run.Click += async (_, _) => await RunPlanAsync().ConfigureAwait(true);

		_root = new ScrollViewer
		{
			Content = new StackPanel
			{
				Spacing = 12,
				Margin = new Thickness(20),
				Children =
				{
					new TextBlock
					{
						Text = "在独立的临时 Edge 窗口中执行受限动作。遇到登录、支付、验证码、文件、下载或权限交互会停止，请直接在 Edge 窗口中处理。关闭会删除临时浏览数据。",
						TextWrapping = TextWrapping.Wrap,
						Opacity = 0.8,
					},
					_status,
					new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _stop } },
					new TextBlock { Text = $"动作计划（JSON 数组，最多 {BrowserTaskLimits.MaxActions} 步）：navigate / click / fill / scroll / wait / read_visible_text", TextWrapping = TextWrapping.Wrap },
					_plan,
					new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _run, _allowInput } },
					_result,
				},
			},
		};
		_controller.Changed += OnControllerChanged;
		Refresh();
	}

	public Control Control => _root;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_controller.Changed -= OnControllerChanged;
		_lifetime.Cancel();
		_lifetime.Dispose();
		_root.Content = null;
	}

	private async Task RunPlanAsync()
	{
		BrowserTaskPlan plan;
		try
		{
			plan = BrowserTaskPlan.Parse(_plan.Text ?? string.Empty);
		}
		catch (BrowserPlanException exception)
		{
			_result.Text = exception.Message;
			return;
		}
		bool allowInput = _allowInput.IsChecked == true;
		_result.Text = "执行中…";
		BrowserRunResult? result = null;
		await RunUiOperationAsync(async token =>
		{
			result = await _controller.RunAsync(plan, allowInput, token).ConfigureAwait(false);
		}).ConfigureAwait(true);
		if (result is not null && Volatile.Read(ref _disposed) == 0)
			_result.Text = result.ToJson().ToJsonString(ResultJsonOptions);
	}

	/// <summary>关闭不受忙碌状态限制：执行中的任务会被取消。</summary>
	private async Task StopAsync()
	{
		try { await Task.Run(_controller.StopAsync).ConfigureAwait(true); }
		catch (ObjectDisposedException) { }
		finally
		{
			if (Volatile.Read(ref _disposed) == 0) Refresh();
		}
	}

	private async Task RunUiOperationAsync(Func<CancellationToken, Task> operation)
	{
		if (_busy || Volatile.Read(ref _disposed) != 0) return;
		_busy = true;
		Refresh();
		try
		{
			CancellationToken token = _lifetime.Token;
			// 浏览器启动与页面操作在后台执行，不阻塞 UI 线程。
			await Task.Run(() => operation(token), token).ConfigureAwait(true);
		}
		catch (OperationCanceledException) { }
		catch (BrowserUnavailableException exception)
		{
			if (Volatile.Read(ref _disposed) == 0) _result.Text = exception.Message;
		}
		catch (InvalidOperationException)
		{
			// 插件停用期间控制器可能已释放；页面只展示固定文案。
			if (Volatile.Read(ref _disposed) == 0) _result.Text = "浏览器操作失败";
		}
		finally
		{
			_busy = false;
			if (Volatile.Read(ref _disposed) == 0) Refresh();
		}
	}

	private void OnControllerChanged()
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		Dispatcher.UIThread.Post(() =>
		{
			if (Volatile.Read(ref _disposed) == 0) Refresh();
		});
	}

	private void Refresh()
	{
		BrowserStatus status = _controller.GetStatus();
		string state = status.State switch
		{
			BrowserSessionState.Starting => "正在启动",
			BrowserSessionState.Running => status.TaskRunning ? "运行中，任务执行中" : "运行中",
			BrowserSessionState.Failed => "启动失败",
			_ => "未启动",
		};
		string detail = status.UnavailableReason ?? status.LastFailure ?? string.Empty;
		_status.Text = detail.Length == 0 ? $"状态：{state}" : $"状态：{state}（{detail}）";
		bool available = status.UnavailableReason is null;
		_start.IsEnabled = available && !_busy && status.State is not (BrowserSessionState.Running or BrowserSessionState.Starting);
		_stop.IsEnabled = status.State is BrowserSessionState.Running or BrowserSessionState.Starting || status.TaskRunning;
		_run.IsEnabled = available && !_busy && !status.TaskRunning;
	}
}
