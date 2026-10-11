using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Nori.Plugins.Browser.Tests;

/// <summary>
/// 宿主外测试时加载插件合同程序集。
/// Nori.PluginSDK 只提供编译期资产，不进入 deps.json；构建目标把它复制到测试输出，这里按名称兜底解析。
/// </summary>
internal static class ContractAssemblyResolver
{
	private const string ContractAssemblyName = "Nori.PluginRuntime";

	[ModuleInitializer]
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "测试进程需要在任何测试类型加载前注册合同程序集解析。")]
	internal static void Register() => AssemblyLoadContext.Default.Resolving += Resolve;

	private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
	{
		if (!string.Equals(name.Name, ContractAssemblyName, StringComparison.Ordinal)) return null;
		string path = Path.Combine(AppContext.BaseDirectory, ContractAssemblyName + ".dll");
		return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
	}
}
