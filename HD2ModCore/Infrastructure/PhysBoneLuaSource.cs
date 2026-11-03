using System.Reflection;
using System.Text;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 物理骨骼运行时的 Lua 载荷来源。
///
/// 两个文件都作为嵌入资源随 Core 程序集发布（见 csproj 里的 <c>PhysBone\*.lua</c>），
/// 这样"转换器 / 部署器 / 外置包导出"用的是同一份源码，不存在版本漂移。
/// </summary>
public static class PhysBoneLuaSource
{
	/// <summary>求解器库（纯 Lua，无依赖）。</summary>
	public const string CompatSuffix = "PhysBone.physbone_compat.lua";

	/// <summary>生产入口（发现 / 调度 / 挂卸钩子）。</summary>
	public const string RuntimeSuffix = "PhysBone.physbone_runtime.lua";

	public static string LoadCompat() => Read(CompatSuffix);

	public static string LoadRuntime() => Read(RuntimeSuffix);

	/// <summary>
	/// 合成**单文件**外置包：把 compat 内联进 runtime，并在末尾自动 install。
	///
	/// 为什么内联而不是两个文件：玩家只要把**一个** .lua 拖进 mod 库就完事，
	/// 不依赖第三方加载器的模块解析规则（不同加载器对 `require` 路径的处理并不一致）。
	/// 代价是两个 Lua 功能各带一份 compat 副本——体积上完全可以接受。
	/// </summary>
	/// <param name="addonId">
	/// 给了就把它写成**第一行**的 <c>-- HD2-Addon: &lt;addonId&gt;</c> 标记。
	/// Boot Mod Loader / Chef Shared Loader / MDL3 都按这个标记识别 addon，
	/// 而且要求它必须是首行——所以这里不能有任何前导空行或 BOM。
	/// </param>
	/// <param name="mode">boot / external，只影响日志里的措辞。</param>
	public static string ComposeStandalonePackage(string? addonId = null, string mode = "external")
	{
		var compat = LoadCompat();
		var runtime = LoadRuntime();
		var builder = new StringBuilder(compat.Length + runtime.Length + 2048);
		if (!string.IsNullOrWhiteSpace(addonId))
		{
			builder.Append("-- HD2-Addon: ").Append(addonId).Append('\n');
		}
		builder.AppendLine("-- HD2 物理骨骼运行时（外置包，单文件）");
		builder.AppendLine("-- 由 HD2 Mod Manager 生成。compat 库已内联，直接放进 mod 库即可。");
		builder.AppendLine("-- 需要游戏里已部署带物理参数的 patch（转换时 embed 过容器）。");
		builder.AppendLine();
		builder.AppendLine("-- ==== physbone_compat.lua（内联） ====");
		builder.AppendLine("local PhysBoneCompat = (function()");
		builder.AppendLine(compat);
		builder.AppendLine("end)()");
		builder.AppendLine();
		builder.AppendLine("-- ==== physbone_runtime.lua ====");
		builder.AppendLine("local PhysBoneRuntime = (function()");
		builder.AppendLine(runtime);
		builder.AppendLine("end)()");
		builder.AppendLine();
		builder.AppendLine("-- ==== 自动安装 ====");
		builder.AppendLine("-- 游戏自身的 VM 里 `stingray` 一定存在；外部加载器环境下同样如此。");
		builder.AppendLine("local ok, err = pcall(function()");
		builder.AppendLine("\tlocal engine = rawget(_G, 'stingray')");
		builder.AppendLine("\tif engine == nil then return end");
		builder.Append("\tPhysBoneRuntime.install(engine, { compat = PhysBoneCompat, mode = '")
			.Append(mode)
			.AppendLine("' })");
		builder.AppendLine("end)");
		builder.AppendLine("if not ok then");
		builder.AppendLine("\tlocal real = rawget(_G, 'print')");
		builder.AppendLine("\tif type(real) == 'function' then real('[physbone] 安装失败: ' .. tostring(err)) end");
		builder.AppendLine("end");
		builder.AppendLine();
		builder.AppendLine("return PhysBoneRuntime");
		return builder.ToString();
	}

	/// <summary>
	/// 单引号字符串转义。求解器名只可能是 `tip` / `chain` 这种短标识符，
	/// 但**不假设调用方一定规矩**——拼进 Lua 源码的任何外部文本都必须转义。
	/// </summary>
	private static string EscapeLuaString(string value)
		=> value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "");

	private static string Read(string suffix)
	{
		var assembly = typeof(PhysBoneLuaSource).Assembly;
		var name = assembly.GetManifestResourceNames()
			.SingleOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal))
			?? throw new InvalidOperationException($"内置物理运行时资源缺失：{suffix}");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置物理运行时资源：{suffix}");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}
}
