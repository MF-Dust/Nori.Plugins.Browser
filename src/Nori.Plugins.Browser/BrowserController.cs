using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Nori.PluginRuntime;

namespace Nori.Plugins.Browser;

/// <summary>浏览器会话生命周期状态。</summary>
public enum BrowserSessionState
{
	/// <summary>没有浏览器会话。</summary>
	Stopped,
	/// <summary>正在启动浏览器。</summary>
	Starting,
	/// <summary>浏览器会话已启动。</summary>
	Running,
	/// <summary>最近一次启动失败。</summary>
	Failed,
}

/// <summary>浏览器状态的脱敏快照；不包含页面内容、地址或 Cookie。</summary>
public sealed record BrowserStatus(BrowserSessionState State, bool TaskRunning, string? UnavailableReason, string? LastFailure)
{
	/// <summary>转为工具结果。</summary>
	public JsonObject ToJson() => new()
	{
		["state"] = State.ToString().ToLowerInvariant(),
		["available"] = UnavailableReason is null,
		["unavailableReason"] = UnavailableReason,
		["taskRunning"] = TaskRunning,
		["lastFailure"] = LastFailure,
	};
}

/// <summary>一次浏览器任务的结果；失败时只携带稳定错误码和可展示消息。</summary>
public sealed record BrowserRunResult(bool Succeeded, int CompletedActions, int TotalActions, string? VisibleText, string? FailureCode, string? Message)
{
	/// <summary>转为工具结果。</summary>
	public JsonObject ToJson()
	{
		JsonObject result = new()
		{
			["ok"] = Succeeded,
			["completedActions"] = CompletedActions,
			["totalActions"] = TotalActions,
		};
		if (VisibleText is not null) result["visibleText"] = VisibleText;
		if (FailureCode is not null) result["failureCode"] = FailureCode;
		if (Message is not null) result["message"] = Message;
		return result;
	}
}

/// <summary>
/// 浏览器自动化控制器。
///
/// 持有至多一个隔离 Edge 会话，并串行执行结构化任务；每个任务受 120 秒总时限约束。
/// 填写动作只在调用方显式允许输入时执行。日志只记录生命周期与稳定错误码，不写入地址、选择器或文本。
/// </summary>
public sealed class BrowserController : IAsyncDisposable
{
	private readonly IBrowserSessionFactory _factory;
	private readonly IPluginLogger? _logger;
	private readonly TimeSpan _taskTimeout;
	private readonly SemaphoreSlim _sessionGate = new(1, 1);
	private readonly SemaphoreSlim _taskGate = new(1, 1);
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _stateGate = new();
	private IBrowserSession? _session;
	private BrowserSessionState _state = BrowserSessionState.Stopped;
	private string? _lastFailure;
	private CancellationTokenSource? _activeTask;
	private int _disposed;

	/// <summary>状态变化时触发；订阅者可能在任意线程收到通知。</summary>
	public event Action? Changed;

	/// <summary>创建控制器。</summary>
	public BrowserController(IBrowserSessionFactory factory, IPluginLogger? logger = null, TimeSpan? taskTimeout = null)
	{
		ArgumentNullException.ThrowIfNull(factory);
		_factory = factory;
		_logger = logger;
		_taskTimeout = taskTimeout ?? BrowserTaskLimits.MaximumDuration;
		if (_taskTimeout <= TimeSpan.Zero || _taskTimeout > BrowserTaskLimits.MaximumDuration)
			throw new ArgumentOutOfRangeException(nameof(taskTimeout));
	}

	/// <summary>读取当前状态。</summary>
	public BrowserStatus GetStatus()
	{
		lock (_stateGate)
		{
			BrowserSessionState state = _state == BrowserSessionState.Running && _session is { IsAlive: false }
				? BrowserSessionState.Stopped
				: _state;
			return new BrowserStatus(state, _activeTask is not null, _factory.UnavailableReason, _lastFailure);
		}
	}

	/// <summary>启动隔离 Edge；已有可用会话时直接返回。</summary>
	public async Task<BrowserStatus> StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
		return GetStatus();
	}

	/// <summary>取消当前任务并关闭 Edge 与临时 profile；重复调用保持幂等。</summary>
	public async Task<BrowserStatus> StopAsync()
	{
		if (Volatile.Read(ref _disposed) != 0) return GetStatus();
		CancelActiveTask();
		await _sessionGate.WaitAsync().ConfigureAwait(false);
		try { await CloseSessionCoreAsync().ConfigureAwait(false); }
		finally { _sessionGate.Release(); }
		Notify();
		return GetStatus();
	}

	/// <summary>
	/// 执行一个浏览器任务；必要时先启动 Edge。
	/// allowInput 为 false 时拒绝包含填写动作的计划。同一时间只运行一个任务。
	/// </summary>
	public async Task<BrowserRunResult> RunAsync(BrowserTaskPlan plan, bool allowInput, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		int total = plan.Actions.Count;
		if (plan.ContainsInput && !allowInput)
			return Failure(0, total, "input_not_allowed", "此入口不允许填写动作，请使用允许输入的浏览器工具");
		if (!await _taskGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
			return Failure(0, total, "busy", "已有浏览器任务正在执行");

		int completed = 0;
		using CancellationTokenSource timeout = new(_taskTimeout);
		using CancellationTokenSource stop = new();
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, timeout.Token, stop.Token);
		lock (_stateGate) _activeTask = stop;
		Notify();
		_logger?.Info($"浏览器任务开始: {total} 个动作");
		try
		{
			IBrowserSession session = await EnsureSessionAsync(linked.Token).ConfigureAwait(false);
			BrowserSessionResult result = await session.ExecuteAsync(plan, new SynchronousProgress(value => Volatile.Write(ref completed, value)), linked.Token).ConfigureAwait(false);
			_logger?.Info($"浏览器任务完成: {result.CompletedActions}/{total}");
			return new BrowserRunResult(true, result.CompletedActions, total, result.VisibleText, null, null);
		}
		catch (BrowserPausedException)
		{
			return Failure(completed, total, "safe_page", "页面包含登录、支付、验证码、文件、下载或权限交互，已停止自动化，请在 Edge 窗口中手动处理");
		}
		catch (BrowserPlanException exception)
		{
			return Failure(completed, total, "policy_rejected", exception.Message);
		}
		catch (BrowserUnavailableException exception)
		{
			return Failure(completed, total, "browser_unavailable", exception.Message);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
		{
			return Failure(completed, total, "timeout", $"浏览器任务超过 {(int)_taskTimeout.TotalSeconds} 秒时限");
		}
		catch (OperationCanceledException)
		{
			return Failure(completed, total, "cancelled", "浏览器任务已取消");
		}
		catch (TimeoutException)
		{
			return Failure(completed, total, "timeout", "页面操作超时");
		}
		catch (PlaywrightException)
		{
			// 不把 Playwright 的地址、选择器或页面信息带回调用方。
			return Failure(completed, total, "execution_failed", "浏览器操作失败");
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return Failure(completed, total, "execution_failed", "浏览器操作失败");
		}
		finally
		{
			lock (_stateGate)
			{
				if (ReferenceEquals(_activeTask, stop)) _activeTask = null;
			}
			_taskGate.Release();
			Notify();
		}
	}

	/// <summary>停止任务并关闭浏览器。</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		await _lifetime.CancelAsync().ConfigureAwait(false);
		await _sessionGate.WaitAsync().ConfigureAwait(false);
		try { await CloseSessionCoreAsync().ConfigureAwait(false); }
		finally { _sessionGate.Release(); }
		// 正在执行的任务会因生命周期取消而尽快返回；信号量交由 GC 回收，避免与其 finally 竞争。
		_lifetime.Dispose();
	}

	private async Task<IBrowserSession> EnsureSessionAsync(CancellationToken cancellationToken)
	{
		await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_session is { IsAlive: true } alive) return alive;
			// 用户手动关闭 Edge 后，先清理旧会话与临时 profile 再重新启动。
			await CloseSessionCoreAsync().ConfigureAwait(false);
			SetState(BrowserSessionState.Starting, null);
			try
			{
				IBrowserSession session = await _factory.StartAsync(cancellationToken).ConfigureAwait(false);
				lock (_stateGate) _session = session;
				SetState(BrowserSessionState.Running, null);
				_logger?.Info("浏览器会话已启动");
				return session;
			}
			catch (OperationCanceledException)
			{
				SetState(BrowserSessionState.Stopped, null);
				throw;
			}
			catch (BrowserUnavailableException exception)
			{
				SetState(BrowserSessionState.Failed, exception.Message);
				throw;
			}
			catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or IOException or UnauthorizedAccessException)
			{
				_logger?.Warn($"浏览器启动失败: {exception.GetType().Name}");
				const string message = "浏览器启动失败，请确认已安装 Microsoft Edge 稳定版";
				SetState(BrowserSessionState.Failed, message);
				throw new BrowserUnavailableException(message);
			}
		}
		finally
		{
			_sessionGate.Release();
		}
	}

	private async Task CloseSessionCoreAsync()
	{
		IBrowserSession? session;
		lock (_stateGate)
		{
			session = _session;
			_session = null;
			_state = BrowserSessionState.Stopped;
			_lastFailure = null;
		}
		if (session is null) return;
		try { await session.DisposeAsync().ConfigureAwait(false); }
		catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or IOException)
		{
			_logger?.Warn($"关闭浏览器会话失败: {exception.GetType().Name}");
		}
		_logger?.Info("浏览器会话已关闭");
	}

	private void CancelActiveTask()
	{
		CancellationTokenSource? active;
		lock (_stateGate) active = _activeTask;
		try { active?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private void SetState(BrowserSessionState state, string? failure)
	{
		lock (_stateGate)
		{
			_state = state;
			_lastFailure = failure;
		}
		Notify();
	}

	private BrowserRunResult Failure(int completed, int total, string code, string message)
	{
		_logger?.Warn($"浏览器任务未完成: {code} ({completed}/{total})");
		return new BrowserRunResult(false, completed, total, null, code, message);
	}

	private void Notify()
	{
		try { Changed?.Invoke(); }
		catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
		{
			// 状态通知不能影响浏览器生命周期。
		}
	}

	/// <summary>同步回调的进度报告器，避免 Progress&lt;T&gt; 投递到同步上下文后乱序。</summary>
	private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
	{
		public void Report(int value) => report(value);
	}
}
