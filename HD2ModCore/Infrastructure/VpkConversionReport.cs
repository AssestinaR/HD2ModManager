using System.Globalization;
using System.Text.Json;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 一次 <c>vpk2patch</c> 用的**转换参数**（只收那些会改变产物内容的部分）。
/// <para>
/// <b>为什么要把它们记下来。</b>转换产物是可再生的，但"同一份来源 VPK + 不同参数"
/// 会得到不同字节：matcap 怎么处理、配件怎么合并、镂空按哪一档落地、底色压不压暗，
/// 四个开关各自改变材质内容甚至 id。重构建要重跑出**和原来一致**的产物，
/// 就必须拿到当初那组参数——否则重构建之后观感变了，而没人会想到是参数丢了。
/// </para>
/// <para>
/// 全部是可空字符串/可空数字：<c>null</c> 表示"当时没传，用的是转换器默认值"。
/// 这是**有意义的信息**，不能和"空字符串"混为一谈（空字符串会被当成显式传入）。
/// </para>
/// </summary>
public sealed record VpkConversionSettings(
	string? Components,
	string? Matcap,
	double? MatcapGain,
	string? Cutout,
	double? AlbedoFactor,
	bool? NormalFlipY = null)
{
	/// <summary>什么都没记（旧产物）：重构建只能按默认值来，并且必须说出来。</summary>
	public static VpkConversionSettings Default { get; } = new(null, null, null, null, null, null);

	/// <summary>把参数摊成命令行参数（顺序固定，便于日志比对；空值跳过，与"没传"语义一致）。</summary>
	public IReadOnlyList<string> ToArguments()
	{
		var arguments = new List<string>(8);
		if (!string.IsNullOrWhiteSpace(Components)) arguments.AddRange(["--components", Components]);
		if (!string.IsNullOrWhiteSpace(Matcap)) arguments.AddRange(["--matcap", Matcap]);
		if (MatcapGain is { } gain)
			arguments.AddRange(["--matcap-gain", gain.ToString("R", CultureInfo.InvariantCulture)]);
		if (!string.IsNullOrWhiteSpace(Cutout)) arguments.AddRange(["--cutout", Cutout]);
		if (AlbedoFactor is { } factor)
			arguments.AddRange(["--albedo-factor", factor.ToString("R", CultureInfo.InvariantCulture)]);
		// 法线 Y 方向：**只在显式翻过时记**。默认是"不翻"，记成 false 会让所有旧产物在
		// 重构建时多出一个参数，日志比对全变。
		if (NormalFlipY == true) arguments.AddRange(["--normal-flip-y", "on"]);
		return arguments;
	}
}

/// <summary>
/// 一次转换里<b>单个 patch</b> 的摘要（每个 patch 对应一个来源 VPK）。
/// <para>
/// 单列出来是为了让"守门"能按 VPK 对 VPK 比较。实测事故（2026-11-03）：
/// 某份报告是 <c>A1主体 + B3</c> 两个 VPK 一次转出来的（101 个材质），
/// 而重构建只重转 B3 一个（98 个），拿 98 比 101 就被误判成"材质塌陷"、
/// 永久拒绝替换 —— 用户点了重构建，产物时间戳一直不变，界面上只看到一句长错误。
/// </para>
/// </summary>
public sealed record VpkConversionPatchSummary(string? Name, int UnitCount, int MaterialCount, int TextureCount);

/// <summary>
/// Mod 目录里 <c>vpk2patch.json</c> 的可读投影。
/// <para>
/// 那个文件是管理人自己写的：内容就是 <c>vpk2patch --json</c> 的整份输出（几十 KB，
/// 逐 patch 的诊断、警告、统计全在里面）。这里只取重构建真正要用的三样东西——
/// <b>命名空间</b>（材质/贴图 id 的一半输入）、<b>转换参数</b>、以及<b>这一批都转了哪些 VPK</b>
/// （找兄弟材质包时用得上）。
/// </para>
/// <para>
/// <b>为什么容忍缺失而不是报错。</b>旧版转换器不写 <c>conversion</c> 块，有些用户的 Mod
/// 是那时候转的。那种 Mod 仍然要能重构建——只是要按默认参数走，并明确告诉用户
/// "这份产物没有记录参数"。
/// </para>
/// </summary>
public sealed record VpkConversionReport(
	string? IdNamespace,
	VpkConversionSettings Settings,
	IReadOnlyList<string> Queue,
	IReadOnlyList<string> Notes,
	int UnitCount = 0,
	int MaterialCount = 0,
	int TextureCount = 0)
{
	/// <summary>
	/// **逐 patch 的摘要**（每个 patch 对应一个来源 VPK）。
	/// <para>
	/// 存在的理由是一个真实事故（2026-11-03）：一份报告可能是**多个 VPK 一次转出来的**
	/// （导入时按"模型主体 + 它的材质包"分组）。而重构建只重转**本 Mod 那一个** VPK，
	/// 于是"新产物 98 个材质 vs 旧报告 101 个"这种**不同范围**的比较会误判成材质塌陷，
	/// 然后永久拒绝替换 —— 表现是"重构建点了没反应，产物时间戳一直不变"。
	/// </para>
	/// <para>所以守门必须**按 VPK 对 VPK** 比，而不是拿"本 VPK 的数"去比"整批的合计"。</para>
	/// </summary>
	public IReadOnlyList<VpkConversionPatchSummary> Patches { get; init; } = [];

	/// <summary>
	/// 找出与给定来源 VPK 对应的那一条 patch 摘要（按文件名匹配，忽略大小写与目录）。
	/// <para>找不到返回 <c>null</c> —— 调用方据此退回"不与旧数据比较"，而不是拿合计硬比。</para>
	/// </summary>
	public VpkConversionPatchSummary? FindPatch(string? vpkFileName)
	{
		if (string.IsNullOrWhiteSpace(vpkFileName))
			return null;
		var target = System.IO.Path.GetFileName(vpkFileName);
		foreach (var patch in Patches)
		{
			if (string.Equals(System.IO.Path.GetFileName(patch.Name ?? string.Empty), target, StringComparison.OrdinalIgnoreCase))
				return patch;
		}

		return null;
	}
	/// <summary>报告文件名。管理器发现"这是 VPK 版 Mod 的产物目录"也靠它。</summary>
	public const string FileName = "vpk2patch.json";

	/// <summary>上限：实测一份 19 个 VPK 的批次报告约 900 KB，留足一个数量级。</summary>
	public const int MaximumBytes = 64 * 1024 * 1024;

	/// <summary><c>true</c> = 这份报告里记了转换参数；<c>false</c> = 旧产物，只能按默认值重跑。</summary>
	public bool HasSettings => Settings != VpkConversionSettings.Default;

	/// <summary>
	/// 生成这份产物的转换器构建时间（UTC ISO-8601，来自 <c>conversion.toolStamp</c>）。
	/// <para>
	/// <b>回答的问题是"这份产物是不是旧代码做的"。</b>实测代价（2026-11-03）：
	/// 改了转换器行为、也点了重构建，但其中一包没被刷到，于是连续三轮测试都在看旧产物，
	/// 还在怀疑代码没生效。当时唯一的线索是文件 mtime，而 mtime 分不清
	/// "重构建过但内容没变"和"根本没重构建"。
	/// </para>
	/// <para><c>null</c> = 旧产物（那时还没写这个字段），一律按"可能过期"处理。</para>
	/// </summary>
	public string? ToolStamp { get; init; }

	/// <summary>
	/// 这份产物是否<b>由给定构建</b>的转换器生成。
	/// <para>
	/// 任一方为 <c>null</c>（旧产物 / 拿不到当前戳）时返回 <c>false</c> —— 判"可能过期"是安全方向：
	/// 误判为过期只是建议重构建一次，误判为新则会让用户对着旧产物查半天。
	/// </para>
	/// </summary>
	public bool IsFromBuild(string? currentStamp)
		=> !string.IsNullOrWhiteSpace(ToolStamp)
			&& !string.IsNullOrWhiteSpace(currentStamp)
			&& string.Equals(ToolStamp, currentStamp, StringComparison.Ordinal);

	public static string GetPath(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		return Path.Combine(modDirectory, FileName);
	}

	/// <summary>
	/// 读一个 Mod 目录的转换报告。<b>读不到 / 读不懂一律返回 <c>null</c></b>：
	/// 调用方据此说"这个 Mod 没有转换报告"，而不是拿半份数据去重跑转换。
	/// </summary>
	public static VpkConversionReport? TryRead(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		return TryReadFile(GetPath(modDirectory));
	}

	public static VpkConversionReport? TryReadFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return null;
			var info = new FileInfo(path);
			if (info.Length is <= 0 or > MaximumBytes) return null;
			return TryParse(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 解析一份报告 JSON。<c>vpk2patch --json</c> 的 stdout 就是这种形状，
	/// 所以重构建可以直接拿进程输出比数字，不必先落盘再读回来。
	/// </summary>
	public static VpkConversionReport? TryParse(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return null;

			var counts = ReadCounts(root);
			return new VpkConversionReport(
				ReadString(root, "idNamespace"),
				ReadSettings(root),
				ReadStrings(root, "queue"),
				ReadStrings(root, "notes"),
				counts.Units,
				counts.Materials,
				counts.Textures)
			{
				ToolStamp = ReadToolStamp(root),
				Patches = ReadPatchSummaries(root),
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// 逐 patch 的统计加总。
	/// <para>
	/// <b>它有什么用。</b>重构建最容易出的事故是"这次没找到同批的材质包"——
	/// 产物里每个 section 的材质会全部塌成同一个兜底材质，而 patch 结构、Unit 数、
	/// 文件大小全都看起来正常。<see cref="MaterialCount"/> 与上一次的数字一比就能发现它。
	/// </para>
	/// </summary>
	private static (int Units, int Materials, int Textures) ReadCounts(JsonElement root)
	{
		if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
			return (0, 0, 0);

		var units = 0;
		var materials = 0;
		var textures = 0;
		foreach (var patch in patches.EnumerateArray())
		{
			units += ReadInt(patch, "units");
			materials += ReadInt(patch, "materials");
			textures += ReadInt(patch, "textures");
		}

		return (units, materials, textures);
	}

	private static int ReadInt(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt32(out var parsed)
			? parsed
			: 0;

	/// <summary>逐 patch 摘要（每个 patch 对应一个来源 VPK）。见 <see cref="VpkConversionReport.Patches"/>。</summary>
	private static IReadOnlyList<VpkConversionPatchSummary> ReadPatchSummaries(JsonElement root)
	{
		if (!root.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
			return [];

		var items = new List<VpkConversionPatchSummary>();
		foreach (var patch in patches.EnumerateArray())
		{
			items.Add(new VpkConversionPatchSummary(
				ReadString(patch, "name"),
				ReadInt(patch, "units"),
				ReadInt(patch, "materials"),
				ReadInt(patch, "textures")));
		}

		return items;
	}

	/// <summary>读 <c>conversion.toolStamp</c>（旧产物没有这个字段 → <c>null</c>）。</summary>
	private static string? ReadToolStamp(JsonElement root)
		=> root.TryGetProperty("conversion", out var conversion) && conversion.ValueKind == JsonValueKind.Object
			? ReadString(conversion, "toolStamp")
			: null;

	private static VpkConversionSettings ReadSettings(JsonElement root)
	{
		// `conversion` 是后加的块。没有它就是旧产物 —— 返回 Default（== 什么都没记）。
		if (!root.TryGetProperty("conversion", out var conversion) || conversion.ValueKind != JsonValueKind.Object)
			return VpkConversionSettings.Default;

		return new VpkConversionSettings(
			ReadString(conversion, "components"),
			ReadString(conversion, "matcap"),
			ReadDouble(conversion, "matcapGain"),
			ReadString(conversion, "cutout"),
			ReadDouble(conversion, "albedoFactor"),
			ReadBool(conversion, "normalFlipY"));
	}

	private static string? ReadString(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() is { Length: > 0 } text ? text : null
			: null;

	private static double? ReadDouble(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
			&& value.TryGetDouble(out var parsed)
			? parsed
			: null;

	/// <summary>
	/// 读一个布尔开关。<b>只有真正的 true/false 才算</b>——缺字段返回 <c>null</c>
	/// （= 当时没传），不能把"没记"当成 false：那只对"默认 false"这个巧合成立。
	/// </summary>
	private static bool? ReadBool(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: null;

	private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
			return [];
		var items = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
				items.Add(text);
		}

		return items;
	}
}
