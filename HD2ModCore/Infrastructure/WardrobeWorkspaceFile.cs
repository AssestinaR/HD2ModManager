using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <c>wardrobe.workspace.json</c> 的读写与校验。规格见
/// <c>HD2RuntimeArmorOverride/notes/wardrobe-schema-v2-20261017.md</c>。
///
/// 这是**权威数据**，不是编译产物：管理器编辑即保存，丢失等于丢用户的勾选。
/// 位置照 <c>armor-registry.workspace.json</c> 的先例放在
/// <c>&lt;mod库&gt;/.hd2modmanager/</c> 下，由管理器编译成
/// <c>&lt;ArmorRegistry&gt;/wardrobe.masks</c>。
///
/// 校验分两档：<see cref="Validate"/> 把问题一次收全（编辑器要在树上标红），
/// <see cref="Parse"/> 在结构有问题时直接抛（读一份坏文件不该得到"半份设置"）。
///
/// This is the authoritative store, not a compiled artefact: the manager saves it on every
/// edit, so losing it means losing the user's checkboxes. It lives under
/// &lt;mod library&gt;/.hd2modmanager/ following the armor-registry.workspace.json precedent,
/// and the manager compiles it into &lt;ArmorRegistry&gt;/wardrobe.masks.
/// </summary>
public static class WardrobeWorkspaceFile
{
	/// <summary>工作区文件名。</summary>
	public const string FileName = "wardrobe.workspace.json";

	/// <summary>
	/// 上限。这是"每件护甲 × 每个 unit × 每个 section"的勾选集合，
	/// 满配也只是几万个整数；4 MiB 足够且能挡住把别的 JSON 误传进来。
	/// </summary>
	public const int MaximumBytes = 4 * 1024 * 1024;

	/// <summary>section 下标上限，与 <c>wardrobe.masks</c> 的语法一致。</summary>
	public const int MaximumSectionIndex = 65535;

	/// <summary>
	/// JSON 选项。命名策略 camelCase；枚举写成 <c>inherit</c> / <c>own</c> 而不是数字——
	/// 这份文件是给人看、也会被人手改的，数字枚举会让"我到底改成几了"变成猜谜。
	/// </summary>
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	// --- 校验 ---------------------------------------------------------------

	/// <summary>
	/// 全部校验规则，一次报全。返回空列表即有效。
	/// </summary>
	public static IReadOnlyList<WardrobeMaskDiagnostic> Validate(WardrobeWorkspaceDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var diagnostics = new List<WardrobeMaskDiagnostic>();
		if (document.Revision < 0)
			diagnostics.Add(WardrobeMaskDiagnostic.Error("revision_invalid", $"revision 必须是非负整数，实际是 {document.Revision}。"));
		if (document.Armours is null)
		{
			diagnostics.Add(WardrobeMaskDiagnostic.Error("armours_missing", "缺少 armours。"));
			return diagnostics;
		}

		var seenArmours = new HashSet<uint>();
		foreach (var armour in document.Armours)
		{
			if (armour is null)
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error("armour_null", "armours 里有空条目。"));
				continue;
			}

			if (!seenArmours.Add(armour.ItemId))
				diagnostics.Add(WardrobeMaskDiagnostic.Error("armour_duplicate", $"护甲 {armour.ItemId} 出现了不止一次。"));
			ValidateArmour(armour, diagnostics);
		}

		return diagnostics;
	}

	private static void ValidateArmour(WardrobeWorkspaceArmour armour, List<WardrobeMaskDiagnostic> diagnostics)
	{
		var units = armour.Units ?? Array.Empty<WardrobeWorkspaceUnit>();
		// 继承 + 自带条目是语义矛盾：继承的含义就是"没有自己的数据"。
		// 放过去的话，编辑器会显示一份、编译会用另一份。
		if (armour.Mode == WardrobeMaskMode.Inherit && units.Any(unit => unit?.Off?.Count > 0))
		{
			diagnostics.Add(WardrobeMaskDiagnostic.Error(
				"armour_mode_contradiction",
				$"护甲 {armour.ItemId} 的模式是 inherit（继承视觉来源），却又写了禁用条目；" +
				"要独立设置请把模式改成 own。"));
		}

		var seenUnits = new HashSet<string>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			if (unit is null)
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error("unit_null", $"护甲 {armour.ItemId} 的 units 里有空条目。"));
				continue;
			}

			if (!WardrobeSectionFingerprint.TryNormalizeUnit(unit.Unit, out var normalized))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"unit_invalid",
					$"护甲 {armour.ItemId} 里的 unit '{unit.Unit}' 不是 16 位 hex。"));
				continue;
			}

			if (!seenUnits.Add(normalized))
			{
				diagnostics.Add(WardrobeMaskDiagnostic.Error(
					"unit_duplicate",
					$"护甲 {armour.ItemId} 里的 unit {normalized} 出现了不止一次；请把它的 section 合到一个条目里。"));
			}

			var seenIndices = new HashSet<int>();
			foreach (var index in unit.Off ?? Array.Empty<int>())
			{
				if (index < 0 || index > MaximumSectionIndex)
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"section_index_out_of_range",
						$"护甲 {armour.ItemId} 的 {normalized} 里 section 下标 {index} 超出 0..{MaximumSectionIndex}。"));
					continue;
				}

				if (!seenIndices.Add(index))
				{
					diagnostics.Add(WardrobeMaskDiagnostic.Error(
						"section_index_duplicate",
						$"护甲 {armour.ItemId} 的 {normalized} 里 section 下标 {index} 重复。"));
				}
			}
		}
	}

	// --- 读写 ---------------------------------------------------------------

	/// <summary>
	/// 规范化：排序 + unit 小写化。序列化也走这里，保证"读进来再写出去"逐字节稳定——
	/// 否则内容没变也会被当成变了，白白触发一次重编。
	/// </summary>
	public static WardrobeWorkspaceDocument Normalize(WardrobeWorkspaceDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var armours = (document.Armours ?? Array.Empty<WardrobeWorkspaceArmour>())
			.Where(armour => armour is not null)
			.OrderBy(armour => armour.ItemId)
			.Select(armour => armour with
			{
				Units = (armour.Units ?? Array.Empty<WardrobeWorkspaceUnit>())
					.Where(unit => unit is not null)
					.Select(unit => unit with
					{
						Unit = WardrobeSectionFingerprint.TryNormalizeUnit(unit.Unit, out var normalized) ? normalized : unit.Unit,
						Off = (unit.Off ?? Array.Empty<int>()).Distinct().Order().ToArray(),
					})
					.OrderBy(unit => unit.Unit, StringComparer.Ordinal)
					.ToArray(),
			})
			.ToArray();
		return new WardrobeWorkspaceDocument(document.Schema, document.Revision, armours);
	}

	public static string Serialize(WardrobeWorkspaceDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		return JsonSerializer.Serialize(Normalize(document), JsonOptions) + "\n";
	}

	/// <summary>解析并校验。任何结构问题都抛，不返回"半份设置"。</summary>
	public static WardrobeWorkspaceDocument Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"wardrobe.workspace.json 超过 {MaximumBytes} 字节上限。");

		WardrobeWorkspaceDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<WardrobeWorkspaceDocument>(text, JsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"wardrobe.workspace.json 不是合法的 JSON：{exception.Message}", exception);
		}

		if (document is null)
			throw new InvalidDataException("wardrobe.workspace.json 内容为空。");
		if (!string.Equals(document.Schema, WardrobeWorkspaceDocument.CurrentSchema, StringComparison.Ordinal))
			throw new InvalidDataException(
				$"wardrobe.workspace.json 的 schema 是 '{document.Schema}'，本程序只认 '{WardrobeWorkspaceDocument.CurrentSchema}'；请重新保存一次。");

		var diagnostics = Validate(document);
		var errors = diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();
		if (errors.Length > 0)
		{
			var builder = new StringBuilder("wardrobe.workspace.json 校验失败：");
			foreach (var error in errors)
				builder.Append('\n').Append(error.Code).Append('：').Append(error.Message);
			throw new InvalidDataException(builder.ToString());
		}

		return Normalize(document);
	}

	/// <summary>读文件；不存在时返回 <see cref="WardrobeWorkspaceDocument.Empty"/>（= 从未编辑过）。</summary>
	public static WardrobeWorkspaceDocument Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!File.Exists(path))
			return WardrobeWorkspaceDocument.Empty;
		var info = new FileInfo(path);
		if (info.Length > MaximumBytes)
			throw new InvalidDataException($"wardrobe.workspace.json 超过 {MaximumBytes} 字节上限：{path}");
		return Parse(File.ReadAllText(path, Encoding.UTF8));
	}

	/// <summary>
	/// 原子写：先写同目录临时文件再替换。管理器编辑即保存，写一半就被读到的话，
	/// 用户会看到一份"随机少了几项"的设置——比报错难查得多。
	/// </summary>
	public static void Write(string path, WardrobeWorkspaceDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(document);
		var normalized = Normalize(document);
		var errors = Validate(normalized).Where(diagnostic => diagnostic.IsError).ToArray();
		if (errors.Length > 0)
			throw new InvalidDataException($"拒绝写出无效的 wardrobe.workspace.json：{errors[0].Code}：{errors[0].Message}");

		var text = Serialize(normalized);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"wardrobe.workspace.json 超过 {MaximumBytes} 字节上限：{path}");
		var fullPath = Path.GetFullPath(path);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporaryPath, fullPath, overwrite: true);
		}
		finally
		{
			// 失败时不要留下临时文件：它们会被下次扫描当成垃圾，也会让目录变脏。
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}

	/// <summary>工作区根目录：<c>&lt;mod库&gt;/.hd2modmanager/</c>，与护甲注册工作区同一个目录。</summary>
	public static string ResolvePath(string modLibraryRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modLibraryRoot);
		return Path.GetFullPath(Path.Combine(modLibraryRoot, ".hd2modmanager", FileName));
	}

	/// <summary>把修订号加一，用于"每次保存都要触发重编"。</summary>
	public static WardrobeWorkspaceDocument NextRevision(WardrobeWorkspaceDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var next = document.Revision == int.MaxValue ? 0 : document.Revision + 1;
		return document with { Revision = next };
	}
}
