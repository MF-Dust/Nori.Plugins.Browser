using Microsoft.Playwright;

namespace Nori.Plugins.Browser;

/// <summary>浏览器会话执行结果；只在内存中传递。</summary>
public sealed record BrowserSessionResult(int CompletedActions, string? VisibleText);

/// <summary>一个已启动的隔离浏览器会话；测试实现不得拉起真实浏览器。</summary>
public interface IBrowserSession : IAsyncDisposable
{
	/// <summary>会话是否仍可用；用户关闭浏览器窗口后为 false。</summary>
	bool IsAlive { get; }

	/// <summary>在当前会话中执行已校验的计划；progress 报告已完成的动作数。</summary>
	Task<BrowserSessionResult> ExecuteAsync(BrowserTaskPlan plan, IProgress<int>? progress, CancellationToken cancellationToken);
}

/// <summary>创建隔离浏览器会话。</summary>
public interface IBrowserSessionFactory
{
	/// <summary>当前环境能否启动会话；不可用时给出可展示原因。</summary>
	string? UnavailableReason { get; }

	/// <summary>启动一个新的隔离会话。</summary>
	Task<IBrowserSession> StartAsync(CancellationToken cancellationToken);
}

/// <summary>使用已安装 Microsoft Edge 的隔离会话工厂；不会下载浏览器或复用默认 profile。</summary>
internal sealed class EdgeBrowserSessionFactory : IBrowserSessionFactory
{
	/// <summary>Playwright 驱动所在目录：Microsoft.Playwright.dll 同级的 .playwright。</summary>
	private static string DriverRoot => Path.Combine(
		Path.GetDirectoryName(typeof(Playwright).Assembly.Location) ?? AppContext.BaseDirectory,
		".playwright");

	public string? UnavailableReason
	{
		get
		{
			if (!OperatingSystem.IsWindows()) return "浏览器自动化仅支持 Windows";
			string root = DriverRoot;
			bool driverPresent = File.Exists(Path.Combine(root, "package", "cli.js"))
				&& File.Exists(Path.Combine(root, "node", "win32_x64", "node.exe"));
			return driverPresent ? null : "插件包缺少 Playwright 驱动，请重新安装插件";
		}
	}

	public async Task<IBrowserSession> StartAsync(CancellationToken cancellationToken)
	{
		if (UnavailableReason is { } reason) throw new BrowserUnavailableException(reason);

		string profileDirectory = Path.Combine(Path.GetTempPath(), $"nori-edge-{Guid.NewGuid():N}");
		Directory.CreateDirectory(profileDirectory);
		IPlaywright? playwright = null;
		IBrowserContext? context = null;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			context = await playwright.Chromium.LaunchPersistentContextAsync(
				profileDirectory,
				new BrowserTypeLaunchPersistentContextOptions
				{
					Channel = "msedge",
					Headless = false,
					AcceptDownloads = false,
					HandleSIGINT = false,
					HandleSIGTERM = false,
					HandleSIGHUP = false,
				}).WaitAsync(cancellationToken).ConfigureAwait(false);
			context.SetDefaultTimeout(BrowserSafetyPolicy.DefaultTimeoutMilliseconds);
			IPage page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
			return new EdgeBrowserSession(playwright, context, page, profileDirectory);
		}
		catch
		{
			if (context is not null)
			{
				try { await context.CloseAsync().ConfigureAwait(false); }
				catch (PlaywrightException) { }
			}
			playwright?.Dispose();
			EdgeBrowserSession.TryDeleteDirectory(profileDirectory);
			throw;
		}
	}
}

/// <summary>浏览器无法启动（未安装 Edge、驱动缺失或平台不支持）。</summary>
public sealed class BrowserUnavailableException(string message) : InvalidOperationException(message);

/// <summary>单个隔离 Edge 会话的受限 DOM 操作面；关闭时删除临时 profile。</summary>
internal sealed class EdgeBrowserSession : IBrowserSession
{
	private readonly IPlaywright _playwright;
	private readonly IBrowserContext _context;
	private readonly IPage _page;
	private readonly string _profileDirectory;
	private readonly BrowserSafetySignals _safetySignals = new();
	private readonly SemaphoreSlim _gate = new(1, 1);
	private int _closed;
	private int _disposed;

	public EdgeBrowserSession(IPlaywright playwright, IBrowserContext context, IPage page, string profileDirectory)
	{
		_playwright = playwright;
		_context = context;
		_page = page;
		_profileDirectory = profileDirectory;
		_context.Close += OnContextClosed;
		_context.Page += OnPage;
		AttachPageHandlers(page, _safetySignals);
	}

	public bool IsAlive => Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _disposed) == 0 && !_page.IsClosed;

	public async Task<BrowserSessionResult> ExecuteAsync(BrowserTaskPlan plan, IProgress<int>? progress, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_safetySignals.Reset();
			string? visibleText = null;
			int completed = 0;
			foreach (BrowserAction action in plan.Actions)
			{
				cancellationToken.ThrowIfCancellationRequested();
				switch (action)
				{
					case BrowserNavigateAction navigate:
						await NavigateAsync(navigate.Url, cancellationToken).ConfigureAwait(false);
						break;
					case BrowserClickAction click:
						await ClickAsync(click.Selector, cancellationToken).ConfigureAwait(false);
						break;
					case BrowserFillAction fill:
						await FillAsync(fill.Selector, fill.Text, cancellationToken).ConfigureAwait(false);
						break;
					case BrowserScrollAction scroll:
						await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
						await _page.Mouse.WheelAsync(0, BrowserSafetyPolicy.ValidateScroll(scroll.Pixels)).WaitAsync(cancellationToken).ConfigureAwait(false);
						break;
					case BrowserWaitAction wait:
						await Task.Delay(BrowserSafetyPolicy.ValidateWait(wait.Milliseconds), cancellationToken).ConfigureAwait(false);
						break;
					case BrowserReadVisibleTextAction:
						await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
						string text = await _page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
						visibleText = BrowserTaskLimits.TruncateVisibleText(text);
						break;
					default:
						throw new BrowserPlanException("浏览器动作类型不在白名单内");
				}
				completed++;
				progress?.Report(completed);
			}
			return new BrowserSessionResult(completed, visibleText);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_context.Close -= OnContextClosed;
		_context.Page -= OnPage;
		try { await _context.CloseAsync().ConfigureAwait(false); }
		catch (PlaywrightException)
		{
			// 浏览器已经崩溃或被用户关闭时仍继续清理临时 profile。
		}
		_playwright.Dispose();
		_gate.Dispose();
		TryDeleteDirectory(_profileDirectory);
	}

	internal static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private async Task NavigateAsync(string url, CancellationToken cancellationToken)
	{
		Uri target = BrowserSafetyPolicy.ValidateNavigation(url);
		await _page.GotoAsync(target.ToString(), new PageGotoOptions
		{
			WaitUntil = WaitUntilState.DOMContentLoaded,
			Timeout = BrowserSafetyPolicy.DefaultTimeoutMilliseconds,
		}).WaitAsync(cancellationToken).ConfigureAwait(false);
		await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task ClickAsync(string selector, CancellationToken cancellationToken)
	{
		ILocator locator = await LocateUniqueVisibleAsync(selector, cancellationToken).ConfigureAwait(false);
		await locator.ClickAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
		await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task FillAsync(string selector, string text, CancellationToken cancellationToken)
	{
		string normalizedText = BrowserSafetyPolicy.ValidateInput(text);
		ILocator locator = await LocateUniqueVisibleAsync(selector, cancellationToken).ConfigureAwait(false);
		await locator.FillAsync(normalizedText).WaitAsync(cancellationToken).ConfigureAwait(false);
		await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>先复核安全页面，再要求选择器恰好命中一个可见元素。</summary>
	private async Task<ILocator> LocateUniqueVisibleAsync(string selector, CancellationToken cancellationToken)
	{
		string normalized = BrowserSafetyPolicy.ValidateSelector(selector);
		await EnsureSafePageAsync(cancellationToken).ConfigureAwait(false);
		ILocator locator = _page.Locator(normalized);
		int count = await locator.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
		if (count != 1) throw new BrowserPlanException($"选择器需要恰好匹配一个元素，实际匹配 {count} 个");
		if (!await locator.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
			throw new BrowserPlanException("选择器匹配的元素不可见");
		return locator;
	}

	private Task EnsureSafePageAsync(CancellationToken cancellationToken) =>
		BrowserSafetyPolicy.EnsureSafePageAsync(_page, _safetySignals, cancellationToken);

	private void OnContextClosed(object? sender, IBrowserContext context) => Volatile.Write(ref _closed, 1);

	private void OnPage(object? sender, IPage page) => AttachPageHandlers(page, _safetySignals);

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "S1854", Justification = "浏览器事件回调参数按 Playwright 事件契约保留，后台任务结果受控丢弃。")]
	private static void AttachPageHandlers(IPage page, BrowserSafetySignals safetySignals)
	{
		page.Dialog += (sender, dialog) =>
		{
			safetySignals.Report(BrowserPauseReason.PermissionDialog);
			_ = IgnoreFailureAsync(dialog.DismissAsync());
		};
		page.FileChooser += (sender, chooser) =>
		{
			safetySignals.Report(BrowserPauseReason.FileChooser);
			_ = IgnoreFailureAsync(chooser.SetFilesAsync(Array.Empty<string>()));
		};
		page.Download += (sender, download) =>
		{
			safetySignals.Report(BrowserPauseReason.Download);
			_ = IgnoreFailureAsync(download.CancelAsync());
		};
	}

	private static async Task IgnoreFailureAsync(Task operation)
	{
		try { await operation.ConfigureAwait(false); }
		catch (PlaywrightException) { }
	}
}
