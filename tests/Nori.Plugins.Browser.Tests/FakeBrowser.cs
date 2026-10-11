namespace Nori.Plugins.Browser.Tests;

/// <summary>不拉起真实浏览器的会话替身。</summary>
internal sealed class FakeBrowserSession(Func<BrowserTaskPlan, IProgress<int>?, CancellationToken, Task<BrowserSessionResult>>? execute = null) : IBrowserSession
{
	public bool Alive { get; set; } = true;
	public int Executions { get; private set; }
	public bool Disposed { get; private set; }

	public bool IsAlive => Alive && !Disposed;

	public async Task<BrowserSessionResult> ExecuteAsync(BrowserTaskPlan plan, IProgress<int>? progress, CancellationToken cancellationToken)
	{
		Executions++;
		if (execute is not null) return await execute(plan, progress, cancellationToken);
		for (int index = 1; index <= plan.Actions.Count; index++) progress?.Report(index);
		return new BrowserSessionResult(plan.Actions.Count, "页面文本");
	}

	public ValueTask DisposeAsync()
	{
		Disposed = true;
		return ValueTask.CompletedTask;
	}
}

/// <summary>按顺序返回预设会话的工厂替身。</summary>
internal sealed class FakeBrowserSessionFactory : IBrowserSessionFactory
{
	private readonly Queue<FakeBrowserSession> _sessions = new();

	public string? UnavailableReason { get; set; }
	public Exception? StartFailure { get; set; }
	public int Starts { get; private set; }
	public List<FakeBrowserSession> Created { get; } = [];

	public FakeBrowserSessionFactory Enqueue(FakeBrowserSession session)
	{
		_sessions.Enqueue(session);
		return this;
	}

	public Task<IBrowserSession> StartAsync(CancellationToken cancellationToken)
	{
		Starts++;
		if (UnavailableReason is { } reason) throw new BrowserUnavailableException(reason);
		if (StartFailure is { } failure) throw failure;
		FakeBrowserSession session = _sessions.Count > 0 ? _sessions.Dequeue() : new FakeBrowserSession();
		Created.Add(session);
		return Task.FromResult<IBrowserSession>(session);
	}
}
