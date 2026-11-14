using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

public enum LowPollutionPrepStatus
{
	/// <summary>已经是最新：跳过。</summary>
	UpToDate,
	/// <summary>需要重编。</summary>
	NeedsRecompile,
	/// <summary>需要重编但不能自动做（这个 Mod 的目标不是自己的素体）。</summary>
	Blocked,
}

/// <param name="Reason">为什么重编 / 跳过 / 不能做，给界面与日志。</param>
public sealed record LowPollutionPrepItem(
	string ModelKey, string ModDirectory, string Name, LowPollutionPrepStatus Status, string Reason);

/// <summary>
/// 低污染映射前的「哪些素体要重编」判断（规划 §12.1）：只看生效层里的产物是不是当前设置下的最新压缩产物，不读游戏数据。
/// <para>
/// 「最新」的判据是布局文件里的<b>指纹</b>：来源 <c>sections.json</c> 的哈希 + 生效的关闭 section + 头身合并。
/// 来源重转换（sections.json 变）、勾选改了、头身合并变了，指纹都会变，于是下次应用会重编。
/// 不能压缩的素体（分体 / 重复部位 / 认不出部位）编译时会留一个带同样指纹的标记文件，同样条件下不重复编译。
/// </para>
/// </summary>
public static class LowPollutionFreshness
{
	public const string UnsupportedMarkerFileName = "low-pollution-unsupported.json";
	private const string FingerprintVersion = "lp-fp/1";

	/// <summary>
	/// 压缩规划器的版本，写进「不可压缩」标记。2 = 支持按体型拆分的素体（规划 §13.5）：
	/// 没有版本或版本低于它的标记是旧规划器因分体放弃时留的，要重编一次。<b>不要动 <see cref="FingerprintVersion"/></b>，否则已压缩的素体全部要重编。
	/// </summary>
	internal const int PlannerVersion = 2;

	public static string Fingerprint(
		string payloadDirectory,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody)
	{
		var builder = new StringBuilder();
		builder.Append(FingerprintVersion).Append('\n').Append(headMergeIntoBody ? "merge" : "split").Append('\n');
		foreach (var pair in (disabledSections ?? new Dictionary<string, IReadOnlyCollection<int>>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (pair.Value.Count == 0) continue;
			builder.Append(pair.Key).Append('=').Append(string.Join(',', pair.Value.Order())).Append('\n');
		}

		var sections = Path.Combine(payloadDirectory, WardrobeSectionMapFile.FileName);
		byte[] sectionBytes;
		try { sectionBytes = File.Exists(sections) ? File.ReadAllBytes(sections) : []; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { sectionBytes = []; }
		using var sha = SHA256.Create();
		builder.Append(Convert.ToHexString(sha.ComputeHash(sectionBytes)));
		return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}

	internal static void WriteUnsupportedMarker(
		string modDirectory,
		string payloadDirectory,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		string reason)
	{
		var path = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), UnsupportedMarkerFileName);
		VpkPayloadSwap.ClearReadOnlyForWrite(path);
		File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
		{
			schema = "hd2mod.low-pollution-unsupported/1",
			plannerVersion = PlannerVersion,
			fingerprint = Fingerprint(payloadDirectory, disabledSections, headMergeIntoBody),
			reason,
		}, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
	}

	private static (string? Fingerprint, int Version) ReadMarker(string overwrite)
	{
		var path = Path.Combine(overwrite, UnsupportedMarkerFileName);
		if (!File.Exists(path)) return (null, 0);
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
			var root = document.RootElement;
			return (
				root.TryGetProperty("fingerprint", out var value) ? value.GetString() : null,
				root.TryGetProperty("plannerVersion", out var version) && version.TryGetInt32(out var number) ? number : 0);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return (null, 0);
		}
	}

	/// <summary>
	/// 对一个素体 Mod 做判断。<paramref name="lowPollution"/> 开：不是最新压缩产物就要重编；
	/// 关：生效层还留着压缩痕迹（布局 / 不可压缩标记）就要重编回普通产物；其余跳过。
	/// </summary>
	public static LowPollutionPrepItem Assess(string modelKey, string modDirectory, bool lowPollution, VpkWardrobeFile wardrobe)
	{
		ArgumentNullException.ThrowIfNull(wardrobe);
		var name = Path.GetFileName(modDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		var layoutPath = Path.Combine(overwrite, LowPollutionLayoutFile.FileName);
		var hasLayout = File.Exists(layoutPath);
		var (markerFingerprint, markerVersion) = ReadMarker(overwrite);
		var hasMarker = File.Exists(Path.Combine(overwrite, UnsupportedMarkerFileName));

		string? reason;
		if (lowPollution)
		{
			var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
			var current = payload is null ? null : Fingerprint(payload, wardrobe.State.Disabled, wardrobe.HeadMergeIntoBody);
			if (hasLayout)
			{
				if (!LowPollutionLayoutReader.TryRead(modDirectory, out var layout, out _) || layout is null)
					reason = "布局文件读不出来";
				else if (layout.Fingerprint is null)
					reason = "布局文件是旧版本写的（没有指纹）";
				else if (layout.HeadMerge != wardrobe.HeadMergeIntoBody)
					reason = "头身合并设置变了";
				else if (!string.Equals(layout.Fingerprint, current, StringComparison.Ordinal))
					reason = "来源或勾选变了";
				else
					return new(modelKey, modDirectory, name, LowPollutionPrepStatus.UpToDate, "已是最新的压缩产物");
			}
			else if (hasMarker && markerVersion < PlannerVersion)
				reason = "「不能压缩」标记是旧版规划器写的（不认按体型分开的素体），按新规则重新压缩一次";
			else if (hasMarker && markerFingerprint is not null && string.Equals(markerFingerprint, current, StringComparison.Ordinal))
				return new(modelKey, modDirectory, name, LowPollutionPrepStatus.UpToDate, "这个素体不能压缩（已记录），按普通素体映射");
			else
				reason = hasMarker ? "来源或勾选变了" : "还没有压缩过";
		}
		else
		{
			if (!hasLayout && !hasMarker)
				return new(modelKey, modDirectory, name, LowPollutionPrepStatus.UpToDate, "已是普通产物");
			reason = "关闭低污染模式，恢复成普通产物";
		}

		// 只对「目标是自己的素体」的 Mod 自动重编；目标是游戏护甲的不替用户改意图。
		var own = RuntimeArmorRegistryService.CustomModelArchiveId(modelKey);
		var target = wardrobe.TargetArmorArchiveId;
		if (!string.IsNullOrWhiteSpace(target) && !string.Equals(target, own, StringComparison.OrdinalIgnoreCase))
			return new(modelKey, modDirectory, name, LowPollutionPrepStatus.Blocked,
				$"需要重编（{reason}），但这个 Mod 的目标不是自己的素体，请先在详情页切回「自己的素体」");
		return new(modelKey, modDirectory, name, LowPollutionPrepStatus.NeedsRecompile, reason);
	}

	/// <summary>
	/// 哪些素体有资格进入重编判断：映射已启用，且至少有一条映射的来源是它（<c>ModelKey</c>）。
	/// 映射页左侧列出来、但一个目标都没关联的素体不在其中，一律不编译（用户 2026-11-14：「如果关联了，才跑，如果只是存在左边，不要跑」）。
	/// </summary>
	public static IReadOnlyList<string> SourceModelKeys(bool mappingsEnabled, IEnumerable<RuntimeArmorRegistryMapping> mappings)
	{
		ArgumentNullException.ThrowIfNull(mappings);
		if (!mappingsEnabled) return [];
		return [.. mappings
			.Where(mapping => mapping.IsModelMapping && !string.IsNullOrWhiteSpace(mapping.ModelKey))
			.Select(mapping => mapping.ModelKey!)
			.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>对本次映射用到的素体逐个判断。素体不在目录里的不出现在结果里（映射本来就会失败）。</summary>
	public static async Task<IReadOnlyList<LowPollutionPrepItem>> PlanAsync(
		string modsDirectory, IEnumerable<string> modelKeys, bool lowPollution, CancellationToken cancellationToken = default)
	{
		var catalog = CustomModelCatalog.Collect(modsDirectory);
		var items = new List<LowPollutionPrepItem>();
		foreach (var key in modelKeys.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!catalog.Entries.TryGetValue(key, out var entry)) continue;
			var wardrobe = await VpkModWardrobeService.ReadAsync(entry.ModDirectory, cancellationToken).ConfigureAwait(false);
			items.Add(Assess(key, entry.ModDirectory, lowPollution, wardrobe));
		}
		return items;
	}
}
