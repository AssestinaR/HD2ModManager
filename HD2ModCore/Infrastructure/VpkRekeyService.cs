using System.IO;
using System.Text.Json;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Infrastructure;

/// <summary>一次换甲的结果。</summary>
public sealed record VpkRekeyOutcome(
	bool Ok,
	string? Error,
	int ReplacedUnits,
	int HiddenMeshes,
	IReadOnlyList<string> TargetArchiveIds,
	IReadOnlyList<string> TargetNames)
{
	public static VpkRekeyOutcome Failed(string error) => new(false, error, 0, 0, [], []);

	/// <summary>
	/// 这次没有跨护甲搬运，而是把来源 patch 原样退回 <c>Overwrite/</c>
	/// （用户选的目标就是这个 Mod 自己的素体）。界面据此换一句话。
	/// </summary>
	public bool RestoredFromSource { get; init; }

	/// <summary>
	/// 换甲过程中"需要用户知道但不阻止成功"的事：某个部位没有来源 Unit 将走隐藏、
	/// 某个来源 Unit 在这件护甲上没有落点（会整份消失）、某部位退到了护甲层承载。
	/// </summary>
	public IReadOnlyList<string> Warnings { get; init; } = [];

	/// <summary>
	/// 头身合并**实际**的结果（CLI 的 <c>headMerge</c> 原样带过来）：
	/// <c>applied</c> / <c>off</c> / <c>custom-model</c> / <c>single-layer</c> / <c>no-helmet-kit</c>。
	/// <para>
	/// <b>为什么要它而不是读开关状态。</b>开关打开不等于合并成功：目标是自己的素体时
	/// 没有头盔 item 可隐藏，目标胸部只有一层时装不下两件几何。界面如果只看开关，
	/// 就会在什么都没发生的场合说"已开启"——用户下次进游戏发现没变化，就再也信不过这个开关。
	/// </para>
	/// </summary>
	public string? HeadMerge { get; init; }
}

/// <summary>
/// 调 <c>hd2mod vpk rekey</c> 把 VPK 模组的 7 个 Unit 换到目标护甲上。
/// <para>
/// <b>为什么走外部进程</b>：换甲要读装备表、读目标 Unit、生成隐藏 Unit，这一整套在 CLI 里
/// 已经跑通了。管理器再实现一遍就是两份会漂移的实现，而漂移的表现是"命令行换出来的能看、
/// 管理器换出来的不能看"。管理器只负责"选哪个目标、什么时候换、换完刷新"。
/// </para>
/// </summary>
public static class VpkRekeyService
{
	/// <summary>
	/// 执行一次换甲。<paramref name="modDirectory"/> 给出时，成功后会把"指向哪件护甲"
	/// 记进 <c>wardrobe.json</c>——组件树的根标题要用它，重开页面时也不必再猜。
	/// </summary>
	/// <param name="disabledSections">
	/// 要烘进产物的禁用 section（**来源** Unit 键空间，与 <c>wardrobe.json</c> 存的一致）。
	/// 交给换甲去做而不是这里事后再烘一遍：只有换甲手里有"来源 → 目标"的完整映射。
	/// </param>
	/// <param name="headMergeIntoBody">
	/// 头身合并：<c>true</c> = 头并进护甲套件的胸部槽位（游戏里那件头盔 item 不再有模型），
	/// <c>false</c> = 老做法（头由头盔套件承载，玩家得穿配套头盔）。
	/// 这是**产物形状**的开关，不是显示开关——两个状态下写出来的 patch 完全不同。
	/// <b>默认关</b>：它改变的是"头画在哪"，必须由用户明确打开。
	/// </param>
	public static async Task<VpkRekeyOutcome> RunAsync(
		string modSelector,
		string? modDirectory,
		VpkHostContext context,
		IReadOnlyList<string> targetArchiveIds,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		Action<string>? onStage,
		CancellationToken cancellationToken = default,
		bool headMergeIntoBody = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		var gameData = context.GameDataFolder;
		if (string.IsNullOrWhiteSpace(gameData) || !Directory.Exists(gameData))
			return VpkRekeyOutcome.Failed("没有配置游戏 data 目录：换甲要读目标护甲与隐藏 Unit。");

		var paths = context.Paths;
		// 位置随布局而变（开发构建在旁边、发布包在 bin\ 下），见 Hd2ModCliLocator。
		var executable = Hd2ModCliLocator.TryResolve();
		if (executable is null)
			return VpkRekeyOutcome.Failed(
				"找不到 hd2mod.exe：换甲依赖它（发布包自带）。"
				+ $"已查找：{Hd2ModCliLocator.DescribeSearchedLocations()}");

		onStage?.Invoke("正在读取目标护甲的 Unit 表");
		var arguments = new List<string>
		{
			"vpk", "rekey", modSelector,
			"--manager-root", paths.AppRootDirectory,
			"--library", paths.ModsRootDirectory ?? paths.AppRootDirectory,
			"--game-data", gameData,
			"--json",
		};
		if (targetArchiveIds.Count != 0)
			arguments.AddRange(["--archive-id", string.Join(',', targetArchiveIds)]);
		// 扁平格式 `unit:1,2;unit:3`：可读、可手写、可贴进 issue，比 JSON 少一层转义。
		var disabled = disabledSections?
			.Where(pair => pair.Value.Count != 0)
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(pair => pair.Key + ":" + string.Join(',', pair.Value.Order()))
			.ToArray();
		if (disabled is { Length: > 0 })
			arguments.AddRange(["--disabled", string.Join(';', disabled)]);
		// 头身合并：显式传 on/off，不依赖 CLI 的默认值——开关存在 Mod 自己身上，
		// 而"默认值将来会不会变"不该影响已经配好的 Mod。
		arguments.AddRange(["--head-merge", headMergeIntoBody ? "on" : "off"]);

		var (exitCode, output, error) = await Hd2ModProcess.RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(output))
			return VpkRekeyOutcome.Failed($"换甲失败（hd2mod 退出码 {exitCode}）：{error.Trim()}");

		try
		{
			using var document = JsonDocument.Parse(output);
			var root = document.RootElement;
			if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
			{
				var message = errorElement.TryGetProperty("message", out var text) ? text.GetString() : null;
				return VpkRekeyOutcome.Failed(message ?? "换甲失败。");
			}

			var outcome = new VpkRekeyOutcome(
				root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
				null,
				ReadInt(root, "replacedUnits"),
				ReadInt(root, "hiddenMeshes"),
				ReadStrings(root, "targets"),
				ReadStrings(root, "targetNames"))
			{
				RestoredFromSource = root.TryGetProperty("restoredFromSource", out var restored)
					&& restored.ValueKind == JsonValueKind.True,
				// 换甲时"某个部位没有落点"只在 CLI 的 warnings 里说，而这一条正是
				// "进游戏少一条胳膊"的唯一线索。以前没往界面传，等于白说。
				Warnings = ReadStrings(root, "warnings"),
				// 头身合并的实际结果：界面据此说话，而不是拿开关状态冒充结果。
				HeadMerge = root.TryGetProperty("headMerge", out var headMerge)
					&& headMerge.ValueKind == JsonValueKind.String
						? headMerge.GetString()
						: null,
			};

			// 记住这次指向了哪件护甲：组件树的根标题要用它，重开页面时也不必再猜。
			if (outcome.Ok && !string.IsNullOrWhiteSpace(modDirectory) && Directory.Exists(modDirectory))
			{
				var file = await VpkModWardrobeService.ReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
				await VpkModWardrobeService.WriteAsync(modDirectory, file with
				{
					TargetArmorArchiveId = outcome.TargetArchiveIds.FirstOrDefault(),
					TargetArmorName = outcome.TargetNames.FirstOrDefault(),
				}, cancellationToken).ConfigureAwait(false);
			}

			return outcome;
		}
		catch (JsonException exception)
		{
			return VpkRekeyOutcome.Failed($"换甲输出无法解析：{exception.Message}");
		}
	}

	private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
			? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(text => text.Length != 0).ToArray()
			: [];

	private static int ReadInt(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
}
