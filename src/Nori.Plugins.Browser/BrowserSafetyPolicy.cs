using Microsoft.Playwright;

namespace Nori.Plugins.Browser;

/// <summary>浏览器页面需要用户接管时使用的稳定原因。</summary>
public enum BrowserPauseReason
{
	/// <summary>页面包含登录、支付、验证码、文件或权限交互。</summary>
	SensitivePage,
	/// <summary>页面打开了文件选择器。</summary>
	FileChooser,
	/// <summary>页面触发了下载。</summary>
	Download,
	/// <summary>页面弹出了对话框或权限请求。</summary>
	PermissionDialog,
}

/// <summary>页面包含敏感交互时停止自动化，交由用户在 Edge 窗口中接管。</summary>
public sealed class BrowserPausedException(BrowserPauseReason reason, string message) : InvalidOperationException(message)
{
	/// <summary>暂停原因。</summary>
	public BrowserPauseReason Reason { get; } = reason;
}

/// <summary>浏览器自动化的固定安全边界。</summary>
public static class BrowserSafetyPolicy
{
	public const int MaxUrlCharacters = 2048;
	public const int MaxSelectorCharacters = 256;
	public const int MaxInputCharacters = 4096;
	public const int MaxScrollPixels = 2_000;
	public const int MaxWaitMilliseconds = 30_000;
	public const int DefaultTimeoutMilliseconds = 10_000;

	private static readonly string[] SensitiveSelectors =
	[
		"input[type='password']",
		"input[autocomplete*='password' i]",
		"input[name*='password' i]",
		"input[name*='passcode' i]",
		"input[autocomplete*='cc-' i]",
		"input[name*='card' i]",
		"input[name*='payment' i]",
		"input[name*='cvv' i]",
		"input[name*='captcha' i]",
		"iframe[src*='captcha' i]",
		"iframe[src*='recaptcha' i]",
		"[data-sitekey]",
		"input[type='file']",
		"[data-permission]",
		"[aria-label*='permission' i]",
	];

	/// <summary>只允许带主机的 HTTP/HTTPS 地址，不允许凭据或外部协议。</summary>
	public static Uri ValidateNavigation(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlCharacters)
			throw new BrowserPlanException("浏览器地址为空或超过长度限制");
		if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			|| string.IsNullOrWhiteSpace(uri.Host)
			|| !string.IsNullOrEmpty(uri.UserInfo))
			throw new BrowserPlanException("浏览器只允许不含凭据的 HTTP 或 HTTPS 地址");
		return uri;
	}

	/// <summary>限制为短 CSS 选择器；执行器不会把它当作脚本执行。</summary>
	public static string ValidateSelector(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSelectorCharacters || value.Contains('\n') || value.Contains('\r'))
			throw new BrowserPlanException("浏览器选择器为空或超过长度限制");
		return value.Trim();
	}

	/// <summary>验证填写文本长度。</summary>
	public static string ValidateInput(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (value.Length > MaxInputCharacters) throw new BrowserPlanException("浏览器输入文本超过长度限制");
		return value;
	}

	/// <summary>限制滚动幅度，避免生成不可控的长距离操作。</summary>
	public static int ValidateScroll(int pixels)
	{
		if (pixels is < -MaxScrollPixels or > MaxScrollPixels || pixels == 0)
			throw new BrowserPlanException("浏览器滚动幅度无效");
		return pixels;
	}

	/// <summary>限制单次等待，取消由调用方的 CancellationToken 控制。</summary>
	public static int ValidateWait(int milliseconds)
	{
		if (milliseconds is < 1 or > MaxWaitMilliseconds)
			throw new BrowserPlanException("浏览器等待时间超过限制");
		return milliseconds;
	}

	/// <summary>按安全策略校验一个已经解析的结构化动作。</summary>
	public static BrowserAction ValidateAction(BrowserAction action)
	{
		ArgumentNullException.ThrowIfNull(action);
		switch (action)
		{
			case BrowserNavigateAction navigate:
				ValidateNavigation(navigate.Url);
				break;
			case BrowserClickAction click:
				ValidateSelector(click.Selector);
				break;
			case BrowserFillAction fill:
				ValidateSelector(fill.Selector);
				ValidateInput(fill.Text);
				break;
			case BrowserScrollAction scroll:
				ValidateScroll(scroll.Pixels);
				break;
			case BrowserWaitAction wait:
				ValidateWait(wait.Milliseconds);
				break;
			case BrowserReadVisibleTextAction:
				break;
			default:
				throw new BrowserPlanException("浏览器动作类型不在白名单内");
		}
		return action;
	}

	/// <summary>在 DOM 动作前后重新检查受保护页面与浏览器安全信号。</summary>
	internal static async Task EnsureSafePageAsync(
		IPage page,
		BrowserSafetySignals? safetySignals,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);
		safetySignals?.ThrowIfPaused();
		foreach (string selector in SensitiveSelectors)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (await page.Locator(selector).CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false) > 0)
				throw new BrowserPausedException(BrowserPauseReason.SensitivePage, "浏览器页面包含登录、支付、验证码、文件或权限交互，需要用户接管");
		}
		safetySignals?.ThrowIfPaused();
	}
}

/// <summary>Page 事件触发的 fail-closed 安全信号；不保存事件正文。</summary>
internal sealed class BrowserSafetySignals
{
	private int _reason = -1;

	/// <summary>记录第一个安全信号。</summary>
	public void Report(BrowserPauseReason reason) =>
		Interlocked.CompareExchange(ref _reason, (int)reason, -1);

	/// <summary>开始新任务前清除上一任务已经由用户处理的信号。</summary>
	public void Reset() => Volatile.Write(ref _reason, -1);

	/// <summary>若会话已触发安全信号则停止任务。</summary>
	public void ThrowIfPaused()
	{
		int value = Volatile.Read(ref _reason);
		if (value < 0) return;
		throw new BrowserPausedException((BrowserPauseReason)value, "浏览器会话触发受保护交互，需要用户接管");
	}
}
