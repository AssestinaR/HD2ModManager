using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 掩码规格（`hd2mod.wardrobe-spec/1`）→ 编译产物（`wardrobe.masks`）。
//
// 为什么要有一层规格文件：掩码里的指纹是**内容指纹**，人写不出来也不该写。
// 规格文件用 `(unit, section 下标)` 这种人类能理解的身份描述"关掉哪几块"，
// 编译时从 `sections.json` 查出指纹。这样：
//   1. 人（和编辑器）永远不用碰指纹；
//   2. 指纹一定来自某一份真实构建产物，不可能凭空写错；
//   3. 重建后指纹变了，重新编译一次就自动跟上。
//
// 规格里还可以声明 `patch.sha256` / `patch.gpuResourcesSha256`：声明了就必须对得上，
// 否则拒绝编译。这是"我这份掩码是照着哪份 patch 做的"这件事的唯一机器可验证形式。
//
// The spec layer exists because fingerprints are content hashes: humans cannot and should
// not write them. The spec names sections as (unit, index) and the compiler looks the
// fingerprint up in sections.json, so a mask can only ever be compiled against a real build.

/// <summary>规格文件里的一个 section 引用：身份只有 unit + 下标，指纹由编译器补。</summary>
public sealed record WardrobeMaskSpecSection(string Unit, int Section);

/// <summary>一个掩码集：一组要隐藏的 section。<c>off</c> 省略即"空集"（显式例外）。</summary>
public sealed record WardrobeMaskSpecSet(
	uint Id,
	IReadOnlyList<WardrobeMaskSpecSection>? Off = null);

/// <summary>一个映射目标：护甲 itemId 指向哪个掩码集。</summary>
public sealed record WardrobeMaskSpecTarget(uint ItemId, uint Set);

/// <summary>规格里可选的构建产物指纹声明。</summary>
public sealed record WardrobeMaskSpecPatch(string? Sha256 = null, string? GpuResourcesSha256 = null);

/// <summary>
/// 规格文件的根。
/// 逐字段 <c>Disallow</c> 未映射成员：这份文件是手写的，把 <c>sections</c> 打成 <c>section</c>
/// 会被默认行为静默忽略，然后编译出一份"看起来对、其实一条都没关"的掩码。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WardrobeMaskSpec(
	string Schema,
	int Revision,
	string? Sections,
	WardrobeMaskSpecPatch? Patch,
	IReadOnlyList<WardrobeMaskSpecSet>? Sets,
	IReadOnlyList<WardrobeMaskSpecTarget>? Targets,
	bool Enabled = true)
{
	/// <summary>当前规格格式标识。</summary>
	public const string CurrentSchema = "hd2mod.wardrobe-spec/1";
}

/// <summary>一条诊断。<c>Severity</c> 只有 <c>error</c> 与 <c>warning</c> 两种。</summary>
public sealed record WardrobeMaskDiagnostic(string Severity, string Code, string Message)
{
	public bool IsError => string.Equals(Severity, "error", StringComparison.Ordinal);

	public static WardrobeMaskDiagnostic Error(string code, string message) => new("error", code, message);

	public static WardrobeMaskDiagnostic Warning(string code, string message) => new("warning", code, message);
}

/// <summary>编译结果：文档 + 全部诊断。有 error 时文档仍然给出，但调用方不应写盘。</summary>
public sealed record WardrobeMaskCompileResult(
	bool IsValid,
	WardrobeMaskDocument Document,
	IReadOnlyList<WardrobeMaskDiagnostic> Diagnostics,
	int SetCount,
	int EntryCount,
	int TargetCount);

/// <summary>某个目标最终隐藏了多少 section、以及哪些 unit 被整块关掉。</summary>
public sealed record WardrobeMaskTargetSummary(
	uint ItemId,
	uint SetId,
	int HiddenSections,
	IReadOnlyList<string> FullyHiddenUnits);

/// <summary>校验结果：掩码是否还跟这份构建产物对得上。</summary>
public sealed record WardrobeMaskValidationResult(
	bool IsValid,
	IReadOnlyList<WardrobeMaskDiagnostic> Diagnostics,
	IReadOnlyList<WardrobeMaskTargetSummary> Targets,
	int EntryCount,
	int ResolvedEntryCount,
	int SetCount,
	int TargetCount);

/// <summary>
/// 把规格编译成掩码，以及把掩码反向校验回映射表。两个方向都用同一套身份规则，
/// 避免"编译时能过、校验时却说找不到"这种自相矛盾。
/// </summary>
public static class WardrobeMaskCompiler
{
	/// <summary>
	/// 规格文件的解析选项。直接复用映射表那一份：两种文件都是手可编辑的 JSON，
	/// 对注释、尾逗号、大小写的容忍度必须一致，否则"能读 A 不能读 B"会让人莫名其妙。
	/// </summary>
	public static JsonSerializerOptions JsonOptions => WardrobeSectionMapFile.JsonOptions;

	/// <summary>解析规格文件文本。JSON 层面的错误统一包成 <see cref="InvalidDataException"/>。</summary>
	public static WardrobeMaskSpec Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		WardrobeMaskSpec? spec;
		try
		{
			spec = JsonSerializer.Deserialize<WardrobeMaskSpec>(text, JsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"wardrobe 规格不是合法的 JSON：{exception.Message}", exception);
		}

		return spec ?? throw new InvalidDataException("wardrobe 规格内容为空。");
	}

	/// <summary>
	/// 规格 + 映射表 → 掩码文档。
	/// 所有问题一次性收集完再返回：一次只报一个错会让人来回改十几遍。
	/// </summary>
	public static WardrobeMaskCompileResult Compile(WardrobeMaskSpec spec, WardrobeSectionMapDocument sections)
	{
		ArgumentNullException.ThrowIfNull(spec);
		ArgumentNullException.ThrowIfNull(sections);
		var diagnostics = new List<WardrobeMaskDiagnostic>();
		if (!string.Equals(spec.Schema, WardrobeMaskSpec.CurrentSchema, StringComparison.Ordinal))
		{
			diagnostics.Add(WardrobeMaskDiagnostic.Error(
				"spec_schema_unsupported",
				$"规格的 schema 是 '{spec.Schema}'，本程序只认 '{WardrobeMaskSpec.CurrentSchema}'。"));
		}

		if (spec.Revision < 0)
			diagnostics.Add(WardrobeMaskDiagnostic.Error("revision_invalid", $"revision 必须是非负整数，实际是 {spec.Revision}。"));

		// 声明了构建产物指纹就必须对得上。这是"这份掩码照着哪份 patch 做的"的唯一机器可验证形式。
		CheckDeclaredFingerprint(spec.Patch?.Sha256, sections.Patch.Sha256, "patch_sha256_mismatch", "patch", diagnostics);
		CheckDeclaredFingerprint(
			spec.Patch?.GpuResourcesSha256,
			sections.Patch.GpuResourcesSha256,
			"patch_gpu_sha256_mismatch",
			"gpu_resources",
			diagnostics);

		var sets = new Dictionary<uint, WardrobeMaskSpecSet>();
		foreach (var set in spec.Sets ?? Array.Empty<WardrobeMaskSpecSet>())
		{
			if (set is null)
				continue;
			if (set.Id > ushort.MaxValue)
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"set_id_out_of_range",
					$"掩码集 ID {set.Id} 超出 0..{ushort.MaxValue}；运行时按 uint16 解析。"));
			else if (!sets.TryAdd(set.Id, set))
				diagnostics.Add(WardrobeMaskDiagnostic.Error("set_id_duplicate", $"掩码集 ID {set.Id} 重复。"));
		}

		var entries = new List<WardrobeMaskEntry>();
		var entrySeen = new HashSet<(uint, string, int, string)>();
		foreach (var set in sets.Values.OrderBy(set => set.Id))
		{
			foreach (var reference in set.Off ?? Array.Empty<WardrobeMaskSpecSection>())
			{
				if (reference is null)
					continue;
				if (!WardrobeSectionFingerprint.TryNormalizeUnit(reference.Unit, out var unit))
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"unit_invalid",
						$"掩码集 {set.Id} 里的 unit '{reference.Unit}' 不是 16 位 hex。"));
					continue;
				}

				var mapUnit = sections.FindUnit(unit);
				if (mapUnit is null)
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"unit_not_in_patch",
						$"掩码集 {set.Id} 引用的 unit {unit} 不在 sections.json 里；这份表对应的是另一份构建产物。"));
					continue;
				}

				var entry = sections.FindSection(unit, reference.Section);
				if (entry is null)
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"section_not_in_unit",
						$"掩码集 {set.Id} 引用的 {unit} 第 {reference.Section} 个 section 不存在；该 unit 只有 {mapUnit.Sections.Count} 个。"));
					continue;
				}

				// 指纹只能来自映射表。规格里就算写了也不采纳，这里没有第二个来源。
				var maskEntry = new WardrobeMaskEntry(set.Id, unit, reference.Section, entry.Fingerprint);
				if (!entrySeen.Add((set.Id, unit, reference.Section, entry.Fingerprint)))
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"entry_duplicate",
						$"掩码集 {set.Id} 里 {unit} 第 {reference.Section} 个 section 出现了不止一次。"));
					continue;
				}

				entries.Add(maskEntry);
			}
		}

		var targetSets = new Dictionary<uint, uint>();
		foreach (var target in spec.Targets ?? Array.Empty<WardrobeMaskSpecTarget>())
		{
			if (target is null)
				continue;
			if (!sets.ContainsKey(target.Set))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"target_set_unknown",
					$"目标 {target.ItemId} 指向掩码集 {target.Set}，但规格里没有声明这个集合；" +
					"要表达\"显式例外但一条都不关\"，请声明一个 off 为空的集合。"));
				continue;
			}

			if (!targetSets.TryAdd(target.ItemId, target.Set))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"target_duplicate",
					$"目标 {target.ItemId} 出现了不止一次。"));
			}
		}

		// 声明了却没有任何目标引用的集合：不是错误（编辑器可能刚删掉目标），但值得提醒。
		foreach (var setId in sets.Keys.OrderBy(id => id))
		{
			if (targetSets.ContainsValue(setId))
				continue;
			diagnostics.Add(WardrobeMaskDiagnostic.Warning(
				"set_unused",
				$"掩码集 {setId} 没有任何目标引用，它隐藏的 section 不会生效。"));
		}

		var document = WardrobeMaskFile.Normalize(new WardrobeMaskDocument(spec.Revision, spec.Enabled, entries, targetSets));
		return new WardrobeMaskCompileResult(
			diagnostics.All(diagnostic => !diagnostic.IsError),
			document,
			diagnostics,
			sets.Count,
			entries.Count,
			targetSets.Count);
	}

	/// <summary>
	/// 掩码 + 映射表索引 → 校验结果。这是"重建之后这份掩码还能用吗"的答案。
	/// 指纹不符不是错误地跳过，而是**必须报出来**：那意味着同一个下标现在指向别的几何。
	/// 接受索引而不是单份文档，是因为一个护甲的 unit 可能由多个 mod 提供（§11 映射表随 mod 走）。
	/// </summary>
	public static WardrobeMaskValidationResult Validate(WardrobeMaskDocument masks, WardrobeSectionIndex sections)
	{
		ArgumentNullException.ThrowIfNull(masks);
		ArgumentNullException.ThrowIfNull(sections);
		var diagnostics = new List<WardrobeMaskDiagnostic>();
		var resolved = 0;
		var setIds = new HashSet<uint>();
		foreach (var entry in masks.Entries)
		{
			setIds.Add(entry.SetId);
			var mapUnit = sections.FindUnit(entry.Unit);
			if (mapUnit is null)
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"unit_not_in_patch",
					$"掩码集 {entry.SetId} 引用的 unit {entry.Unit} 不在 sections.json 里。"));
				continue;
			}

			var section = sections.FindSection(entry.Unit, entry.SectionIndex);
			if (section is null)
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"section_not_in_unit",
					$"掩码集 {entry.SetId} 引用的 {entry.Unit} 第 {entry.SectionIndex} 个 section 不存在；该 unit 只有 {mapUnit.Sections.Count} 个。"));
				continue;
			}

			if (!string.Equals(section.Fingerprint, entry.Fingerprint, StringComparison.Ordinal))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"fingerprint_mismatch",
					$"掩码集 {entry.SetId} 的 {entry.Unit} 第 {entry.SectionIndex} 个 section 指纹不符：" +
					$"掩码里是 {entry.Fingerprint}，构建产物里是 {section.Fingerprint}；这份掩码是照着另一份 patch 编的。"));
				continue;
			}

			resolved++;
		}

		foreach (var setId in setIds.OrderBy(id => id))
		{
			if (masks.TargetSets.Values.Contains(setId))
				continue;
			diagnostics.Add(WardrobeMaskDiagnostic.Warning(
				"set_unreferenced",
				$"掩码集 {setId} 隐藏了 section，但没有任何目标引用它，不会生效。"));
		}

		var view = WardrobeMaskView.Create(masks);
		var targets = new List<WardrobeMaskTargetSummary>(masks.TargetSets.Count);
		foreach (var pair in masks.TargetSets.OrderBy(pair => pair.Key))
		{
			var hidden = 0;
			var fullyHidden = new List<string>();
			foreach (var unit in sections.Units)
			{
				var unitHidden = 0;
				foreach (var section in unit.Sections)
				{
					if (view.IsDisabled(pair.Key, unit.FileId, section.Index, section.Fingerprint))
						unitHidden++;
				}

				hidden += unitHidden;
				if (unit.Sections.Count > 0 && unitHidden == unit.Sections.Count)
					fullyHidden.Add(unit.FileId);
			}

			// 空集不是问题，是"显式例外"的正常落点：目标声明了掩码集，但一块都不关。
			if (!setIds.Contains(pair.Value))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Warning(
					"target_set_empty",
					$"目标 {pair.Key} 指向掩码集 {pair.Value}，该集合没有隐藏任何 section（显式例外，全部可见）。"));
			}

			targets.Add(new WardrobeMaskTargetSummary(pair.Key, pair.Value, hidden, fullyHidden));
		}

		return new WardrobeMaskValidationResult(
			diagnostics.All(diagnostic => !diagnostic.IsError),
			diagnostics,
			targets,
			masks.Entries.Count,
			resolved,
			setIds.Count,
			masks.TargetSets.Count);
	}

	private static void CheckDeclaredFingerprint(
		string? declared,
		string? actual,
		string code,
		string label,
		List<WardrobeMaskDiagnostic> diagnostics)
	{
		if (string.IsNullOrWhiteSpace(declared))
			return;
		if (string.IsNullOrWhiteSpace(actual))
		{
			diagnostics.Add(WardrobeMaskDiagnostic.Error(
				code,
				$"规格声明了 {label} 的 sha256，但 sections.json 没有记录这一项；无法核对。"));
			return;
		}

		if (!string.Equals(declared.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(WardrobeMaskDiagnostic.Error(
				code,
				$"{label} 的 sha256 不符：规格里是 {declared.Trim()}，构建产物里是 {actual.Trim()}；这份掩码不是照着这份 patch 编的。"));
		}
	}

	/// <summary>把诊断渲染成一行一条的文本，供 CLI 非 <c>--json</c> 输出使用。</summary>
	public static string Describe(IReadOnlyList<WardrobeMaskDiagnostic> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(diagnostics);
		if (diagnostics.Count == 0)
			return "无问题。";
		var builder = new StringBuilder();
		foreach (var diagnostic in diagnostics)
			builder.Append(diagnostic.Severity).Append(' ').Append(diagnostic.Code).Append('：').Append(diagnostic.Message).Append('\n');
		return builder.ToString().TrimEnd('\n');
	}
}
