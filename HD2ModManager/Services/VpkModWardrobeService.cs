using System.IO;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

/// <summary>
/// <c>mods/&lt;VPK&gt;/wardrobe.json</c> 的内容：这个 VPK 模组的组件开关。
/// <para>
/// <b>为什么把"卡片是否启用"也放这里</b>：它和勾选状态是同一件事的两半——都只对这一个
/// 模组有意义。分两个文件会出现"卡片开了但勾选丢了"这种半截状态。
/// </para>
/// </summary>
public sealed record VpkWardrobeFile(bool ComponentTreeEnabled, VpkWardrobeState State)
{
	public static VpkWardrobeFile Default { get; } = new(false, VpkWardrobeState.Empty);
}

/// <summary>
/// VPK 模组的组件树与烘焙。
/// <para>
/// <b>为什么状态文件放在 Mod 目录而不是注册表</b>：这份开关只对<b>这一个</b> Mod 有意义，
/// 而且它描述的是"这个 patch 里哪几块几何不画"，不是"哪件护甲隐藏了什么"。放进注册表
/// 会和衣柜的护甲掩码混成一件事，切换 Mod 就会互相污染。
/// </para>
/// <para>
/// <b>为什么读 Overwrite 优先</b>：换过目标护甲之后，实际生效的是 <c>Overwrite/</c> 下那份
/// （它顶掉同名 patch）。组件树必须画"正在生效的那份"的 section，否则勾选会烘到一份
/// 根本不部署的 patch 上。
/// </para>
/// </summary>
public sealed class VpkModWardrobeService
{
	public const string StateFileName = "wardrobe.json";
	private const string Schema = "hd2mod.vpk-wardrobe/1";

	/// <summary>基础 7 槽的判据：undergarment 层 + 头盔。其余（护肩、护甲板…）不参与组件开关。</summary>
	public static bool IsBaseSlotUnit(WardrobeSectionMapUnit unit) => VpkModWardrobeTree.IsBaseSlotUnit(unit);

	public static string GetStatePath(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		return Path.Combine(modDirectory, StateFileName);
	}

	/// <summary>
	/// 正在生效的 patch 目录：<c>Overwrite/</c>（若其中有 sections.json）优先，否则 <c>vpk2patch/</c>。
	/// </summary>
	public static string? ResolveEffectivePatchDirectory(string modDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		var overwrite = Path.Combine(modDirectory, ModNodePayloadLayout.OverwriteDirectoryName);
		if (File.Exists(Path.Combine(overwrite, WardrobeSectionMapFile.FileName)))
			return overwrite;
		var payload = Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName);
		if (File.Exists(Path.Combine(payload, WardrobeSectionMapFile.FileName)))
			return payload;
		return null;
	}

	public static async Task<WardrobeSectionMapDocument?> ReadSectionMapAsync(string modDirectory, CancellationToken cancellationToken = default)
	{
		var directory = ResolveEffectivePatchDirectory(modDirectory);
		if (directory is null) return null;
		try
		{
			return await WardrobeSectionMapFile
				.ReadAsync(Path.Combine(directory, WardrobeSectionMapFile.FileName), cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
		{
			return null;
		}
	}

	public static async Task<VpkWardrobeFile> ReadAsync(string modDirectory, CancellationToken cancellationToken = default)
	{
		var path = GetStatePath(modDirectory);
		if (!File.Exists(path)) return VpkWardrobeFile.Default;
		try
		{
			await using var stream = File.OpenRead(path);
			using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
			var root = document.RootElement;
			var enabled = root.TryGetProperty("componentTreeEnabled", out var flag) && flag.ValueKind == JsonValueKind.True;
			var disabled = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
			if (root.TryGetProperty("disabled", out var map) && map.ValueKind == JsonValueKind.Object)
			{
				foreach (var unit in map.EnumerateObject())
				{
					var indices = unit.Value.EnumerateArray()
						.Select(item => item.TryGetInt32(out var value) ? value : -1)
						.Where(value => value >= 0)
						.Order()
						.ToArray();
					if (indices.Length != 0) disabled[unit.Name] = indices;
				}
			}

			return new VpkWardrobeFile(enabled, new VpkWardrobeState(disabled));
		}
		catch (Exception exception) when (exception is IOException or JsonException)
		{
			return VpkWardrobeFile.Default;
		}
	}

	public static async Task WriteAsync(string modDirectory, VpkWardrobeFile file, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(file);
		var path = GetStatePath(modDirectory);
		var payload = new
		{
			schema = Schema,
			componentTreeEnabled = file.ComponentTreeEnabled,
			disabled = file.State.Disabled.ToDictionary(
				pair => pair.Key,
				pair => pair.Value.Order().ToArray(),
				StringComparer.Ordinal),
		};
		var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
		File.Move(temporary, path, overwrite: true);
	}

	/// <summary>只保留基础 7 槽的 unit，再交给 <see cref="WardrobeTree.Build"/> 画树。</summary>
	public static WardrobeTreeNode BuildTree(string label, WardrobeSectionMapDocument map)
		=> VpkModWardrobeTree.Build(label, map);

	/// <summary>
	/// 把当前状态烘进正在生效的那份 patch。**只在用户点"应用"时调用**：
	/// 勾选是编辑意图，烘焙是落盘，两者分开才能"乱点一通、一次写盘"。
	/// </summary>
	public static async Task<WardrobeSectionBakeResult> ApplyAsync(
		string modDirectory,
		VpkWardrobeState state,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(state);
		var directory = ResolveEffectivePatchDirectory(modDirectory)
			?? throw new InvalidOperationException("这个 Mod 里没有可烘焙的 patch（缺 sections.json）。");
		var map = await ReadSectionMapAsync(modDirectory, cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException("sections.json 读不出来，无法烘焙。");
		var patch = FindPatchFile(directory)
			?? throw new InvalidOperationException("patch 目录里没有 patch 文件。");

		return await WardrobeSectionBaker.BakeAsync(patch, map, state.Disabled, cancellationToken).ConfigureAwait(false);
	}

	public static string? FindPatchFile(string directory)
	{
		if (!Directory.Exists(directory)) return null;
		return Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Where(path => !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();
	}

	/// <summary>这个 Mod 是不是 VPK 版（有 <c>vpk2patch/</c> 载荷）。</summary>
	public static bool IsVpkMod(string modDirectory)
		=> Directory.Exists(Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName));
}
