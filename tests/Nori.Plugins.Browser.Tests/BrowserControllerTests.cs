namespace Nori.Plugins.Browser.Tests;

/// <summary>浏览器控制器生命周期与失败归类测试；全部使用替身会话。</summary>
public sealed class BrowserControllerTests
{
	private static BrowserTaskPlan ReadPlan() => new([new BrowserNavigateAction("https://example.test"), new BrowserReadVisibleTextAction()]);

	private static BrowserTaskPlan FillPlan() => new([new BrowserFillAction("#q", "text")]);

	[Fact]
	public async Task 任务按需启动会话并返回可见文本()
	{
		FakeBrowserSessionFactory factory = new();
		await using BrowserController controller = new(factory);

		BrowserRunResult result = await controller.RunAsync(ReadPlan(), allowInput: false);

		Assert.True(result.Succeeded);
		Assert.Equal(2, result.CompletedActions);
		Assert.Equal("页面文本", result.VisibleText);
		Assert.Equal(1, factory.Starts);
		Assert.Equal(BrowserSessionState.Running, controller.GetStatus().State);

		await controller.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal(1, factory.Starts);
	}

	[Fact]
	public async Task 不允许输入的入口拒绝填写动作且不启动浏览器()
	{
		FakeBrowserSessionFactory factory = new();
		await using BrowserController controller = new(factory);

		BrowserRunResult rejected = await controller.RunAsync(FillPlan(), allowInput: false);
		Assert.False(rejected.Succeeded);
		Assert.Equal("input_not_allowed", rejected.FailureCode);
		Assert.Equal(0, factory.Starts);

		Assert.True((await controller.RunAsync(FillPlan(), allowInput: true)).Succeeded);
	}

	[Fact]
	public async Task 同一时间只允许一个任务()
	{
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeBrowserSessionFactory factory = new FakeBrowserSessionFactory().Enqueue(new FakeBrowserSession(async (plan, _, token) =>
		{
			entered.TrySetResult();
			await release.Task.WaitAsync(token);
			return new BrowserSessionResult(plan.Actions.Count, null);
		}));
		await using BrowserController controller = new(factory);

		Task<BrowserRunResult> first = controller.RunAsync(ReadPlan(), allowInput: false);
		await entered.Task;
		Assert.True(controller.GetStatus().TaskRunning);
		BrowserRunResult busy = await controller.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal("busy", busy.FailureCode);

		release.SetResult();
		Assert.True((await first).Succeeded);
		Assert.False(controller.GetStatus().TaskRunning);
	}

	[Fact]
	public async Task 超时与停止分别归类()
	{
		FakeBrowserSessionFactory factory = new FakeBrowserSessionFactory()
			.Enqueue(new FakeBrowserSession(async (_, progress, token) =>
			{
				progress?.Report(1);
				await Task.Delay(Timeout.Infinite, token);
				return new BrowserSessionResult(0, null);
			}));
		await using BrowserController controller = new(factory, taskTimeout: TimeSpan.FromMilliseconds(100));

		BrowserRunResult timedOut = await controller.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal("timeout", timedOut.FailureCode);
		Assert.Equal(1, timedOut.CompletedActions);

		FakeBrowserSessionFactory slowFactory = new FakeBrowserSessionFactory().Enqueue(new FakeBrowserSession(async (_, _, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new BrowserSessionResult(0, null);
		}));
		await using BrowserController stoppable = new(slowFactory);
		Task<BrowserRunResult> running = stoppable.RunAsync(ReadPlan(), allowInput: false);
		while (!stoppable.GetStatus().TaskRunning || slowFactory.Created.Count == 0) await Task.Delay(10);
		await stoppable.StopAsync();
		Assert.Equal("cancelled", (await running).FailureCode);
		Assert.True(slowFactory.Created[0].Disposed);
		Assert.Equal(BrowserSessionState.Stopped, stoppable.GetStatus().State);
	}

	[Fact]
	public async Task 安全页面与策略错误归类为固定错误码()
	{
		FakeBrowserSessionFactory factory = new FakeBrowserSessionFactory()
			.Enqueue(new FakeBrowserSession((_, _, _) => throw new BrowserPausedException(BrowserPauseReason.SensitivePage, "x")));
		await using BrowserController controller = new(factory);
		Assert.Equal("safe_page", (await controller.RunAsync(ReadPlan(), allowInput: false)).FailureCode);

		FakeBrowserSessionFactory selectorFactory = new FakeBrowserSessionFactory()
			.Enqueue(new FakeBrowserSession((_, _, _) => throw new BrowserPlanException("选择器需要恰好匹配一个元素")));
		await using BrowserController selector = new(selectorFactory);
		BrowserRunResult rejected = await selector.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal("policy_rejected", rejected.FailureCode);
		Assert.Equal("选择器需要恰好匹配一个元素", rejected.Message);
	}

	[Fact]
	public async Task 浏览器不可用或启动失败时不泄露内部异常()
	{
		FakeBrowserSessionFactory unavailable = new() { UnavailableReason = "插件包缺少 Playwright 驱动，请重新安装插件" };
		await using BrowserController first = new(unavailable);
		BrowserRunResult missing = await first.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal("browser_unavailable", missing.FailureCode);
		Assert.Equal(unavailable.UnavailableReason, first.GetStatus().UnavailableReason);

		FakeBrowserSessionFactory failing = new() { StartFailure = new InvalidOperationException("C:\\secret\\path") };
		await using BrowserController second = new(failing);
		BrowserRunResult failed = await second.RunAsync(ReadPlan(), allowInput: false);
		Assert.Equal("browser_unavailable", failed.FailureCode);
		Assert.DoesNotContain("secret", failed.Message, StringComparison.Ordinal);
		Assert.Equal(BrowserSessionState.Failed, second.GetStatus().State);
		await Assert.ThrowsAsync<BrowserUnavailableException>(() => second.StartAsync());
	}

	[Fact]
	public async Task 用户关闭浏览器后下次任务重新启动()
	{
		FakeBrowserSessionFactory factory = new();
		await using BrowserController controller = new(factory);
		await controller.StartAsync();
		factory.Created[0].Alive = false;
		Assert.Equal(BrowserSessionState.Stopped, controller.GetStatus().State);

		Assert.True((await controller.RunAsync(ReadPlan(), allowInput: false)).Succeeded);
		Assert.Equal(2, factory.Starts);
		Assert.True(factory.Created[0].Disposed);
	}

	[Fact]
	public async Task 释放时关闭会话且停止幂等()
	{
		FakeBrowserSessionFactory factory = new();
		BrowserController controller = new(factory);
		await controller.StartAsync();
		await controller.StopAsync();
		await controller.StopAsync();
		await controller.StartAsync();
		await controller.DisposeAsync();
		await controller.DisposeAsync();

		Assert.All(factory.Created, session => Assert.True(session.Disposed));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.RunAsync(ReadPlan(), allowInput: false));
	}
}
