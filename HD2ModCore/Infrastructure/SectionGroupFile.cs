using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// <c>components.json</c> 的读写。与 <c>sections.json</c> 放在同一个 patch 目录里。
/// <para>
/// <b>文件名是历史包袱。</b>它现在装的是 section 组表，不叫 <c>section-groups.json</c> 是因为
/// 改名要迁移库里 9 份已有文件，而这件事没有任何用户可见的收益。改名留给后续的命名债
/// （和 <c>vpk2patch/</c> 一样）。
/// </para>
/// <para>
/// 读取对<b>坏文件宽容</b>（返回 <c>null</c>，由调用方决定怎么提示），写入是原子的。
/// 这条不对称是刻意的：组表是可选增强，一份坏掉的表不该让整个 Mod 详情页打不开；
/// 而写坏一份表会让用户下次打开时"组凭空消失"，所以写必须要么成功要么不留痕。
/// </para>
/// <para>
/// <b>旧格式只读不写。</b><c>hd2mod.vpk-components/1</c>（<c>switches</c> / <c>disabled</c>）
/// 读进来就升成 <c>hd2mod.section-groups/1</c>（<c>groups</c> / <c>members</c>），
/// 下次保存自动变成新格式。所以不需要迁移脚本，也不会出现"两份格式并存"。
/// </para>
/// </summary>
public static class SectionGroupFile
{
	public const string FileName = "components.json";

	/// <summary>上限：实测 30 个组约 8 KiB，留两个数量级。</summary>
	public const int MaximumBytes = 4 * 1024 * 1024;

	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		// 组名是中文（`猫耳`、`披风`）。这份文件是给人看、给人 grep 的，不转义。
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		// source 写成 "noDraw" 而不是 0：手改文件时能看懂，出问题时也能直接 grep。
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	// ── JSON 形态（与领域模型分开：要同时认两套键名，且不能把算出来的属性写进文件）──

	private sealed record TargetDto(string? Unit, IReadOnlyList<int>? Sections);

	private sealed record GroupDto(
		string? Id,
		string? Name,
		SectionGroupSource? Source,
		string? ExclusiveGroup,
		string? SourceVpk,
		IReadOnlyList<string>? Materials,
		IReadOnlyList<TargetDto>? Members,
		// 旧格式的键名。**只读不写**，写出时永远为 null 而被 WhenWritingNull 丢掉。
		IReadOnlyList<TargetDto>? Disabled,
		string? Key);

	private sealed record UnmatchedDto(string? Name, string? SourceVpk, string? Reason);

	private sealed record HiddenDto(string? Name, string? Key);

	private sealed record DocumentDto(
		string? Schema,
		IReadOnlyList<GroupDto>? Groups,
		// 旧格式的键名。同上，只读不写。
		IReadOnlyList<GroupDto>? Switches,
		IReadOnlyList<UnmatchedDto>? Unmatched,
		IReadOnlyList<HiddenDto>? Hidden);

	public static string Serialize(SectionGroupDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var dto = new DocumentDto(
			SectionGroupDocument.CurrentSchema,
			[.. document.Groups.Select(ToDto)],
			null,
			document.Unmatched.Count == 0
				? null
				: [.. document.Unmatched.Select(item => new UnmatchedDto(item.Name, item.SourceVpk, item.Reason))],
			document.Hidden.Count == 0
				? null
				: [.. document.Hidden.Select(item => new HiddenDto(item.Name, item.Key))]);
		return JsonSerializer.Serialize(dto, JsonOptions) + "\n";
	}

	/// <summary>解析并校验版本。不合法就抛——写入方自己产的文件不该悄悄失效。</summary>
	public static SectionGroupDocument Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"{FileName} 超过 {MaximumBytes} 字节上限。");

		DocumentDto? dto;
		try
		{
			dto = JsonSerializer.Deserialize<DocumentDto>(text, JsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"{FileName} 不是合法的 JSON：{exception.Message}", exception);
		}

		if (dto is null)
			throw new InvalidDataException($"{FileName} 内容为空。");
		if (string.IsNullOrWhiteSpace(dto.Schema))
			throw new InvalidDataException($"{FileName} 缺少 schema。");
		if (!SectionGroupDocument.ReadableSchemas.Contains(dto.Schema, StringComparer.Ordinal))
			throw new InvalidDataException(
				$"{FileName} 的 schema 是 '{dto.Schema}'，本程序只认 "
				+ $"{string.Join("、", SectionGroupDocument.ReadableSchemas.Select(item => $"'{item}'"))}；"
				+ "请重新导入一次这个 Mod。");

		// 新键优先，旧键兜底。两套键同时存在说明文件被人手改坏了，此时以新键为准。
		var groups = dto.Groups ?? dto.Switches ?? [];
		var unmatched = dto.Unmatched ?? [];
		var document = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			[.. groups.Select(FromDto)],
			[.. unmatched
				.Where(item => !string.IsNullOrWhiteSpace(item.Name))
				.Select(item => new SectionGroupUnmatched(
					item.Name!,
					item.SourceVpk ?? string.Empty,
					item.Reason ?? string.Empty))]);
		return document with
		{
			Hidden = [.. (dto.Hidden ?? []).Where(item => !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Key))
				.Select(item => new SectionGroupTombstone(item.Name ?? string.Empty, item.Key))],
		};
	}

	/// <summary>读一份组表；文件不存在或读不懂都返回 <c>null</c>（调用方据此隐藏组区）。</summary>
	public static async ValueTask<SectionGroupDocument?> TryReadAsync(
		string directory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(directory))
			return null;
		var path = Path.Combine(directory, FileName);
		if (!File.Exists(path))
			return null;
		try
		{
			var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
			return Parse(text);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 把组表里的 Unit 键按 <paramref name="unitMap"/>（旧 → 新）翻译一遍。
	/// <para>
	/// <b>为什么必须翻译。</b>组的成员是 <c>(unit, section)</c>，而换目标护甲会把 Unit 换到
	/// 目标护甲自己的键上。不翻译的话，换一次甲之后整份组表就指向了不存在的 Unit——
	/// 界面只能显示"这些组已经失效"，用户辛苦建的组白设了。
	/// </para>
	/// <para>
	/// 映射不到目标的组<b>保留但清空成员</b>：名字与材质信息仍然有用（说明这个部件在这个目标上
	/// 没有落点），清空成员让界面能明确说"它现在无处可关"，而不是假装能用。
	/// </para>
	/// </summary>
	public static SectionGroupDocument Translate(
		SectionGroupDocument document,
		IReadOnlyDictionary<string, string> unitMap)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(unitMap);
		if (unitMap.Count == 0)
			return document with { Groups = [.. document.Groups.Select(group => group with { Members = [] })] };

		var groups = new List<SectionGroup>(document.Groups.Count);
		foreach (var group in document.Groups)
		{
			var targets = new List<SectionGroupTarget>(group.Members.Count);
			foreach (var target in group.Members)
			{
				if (!unitMap.TryGetValue(target.Unit, out var mapped))
					continue;
				// 两个来源 Unit 可能映射到同一个目标 Unit（合并），那样要把 section 并起来。
				var existing = targets.FirstOrDefault(candidate =>
					string.Equals(candidate.Unit, mapped, StringComparison.OrdinalIgnoreCase));
				if (existing is null)
					targets.Add(new SectionGroupTarget(mapped, [.. target.Sections.Order()]));
				else
					targets[targets.IndexOf(existing)] = existing with
					{
						Sections = [.. existing.Sections.Concat(target.Sections).Distinct().Order()],
					};
			}

			groups.Add(group with
			{
				Members = [.. targets.OrderBy(target => target.Unit, StringComparer.Ordinal)],
			});
		}

		return document with { Groups = groups };
	}

	/// <summary>原子写：先写同目录临时文件再替换，避免详情页读到半份表。</summary>
	public static async ValueTask WriteAsync(
		string directory,
		SectionGroupDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentNullException.ThrowIfNull(document);
		// 落盘前把现算的 id 固化下来：这样下次改名不会让 id 跟着变。
		var stamped = document with
		{
			Groups = [.. document.Groups.Select(group =>
				string.IsNullOrEmpty(group.Id) ? group with { Id = group.EffectiveId } : group)],
		};
		var text = Serialize(stamped);
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"{FileName} 超过 {MaximumBytes} 字节上限：{directory}");

		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, FileName);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
				.ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static GroupDto ToDto(SectionGroup group)
		=> new(
			group.EffectiveId,
			group.Name,
			group.Source,
			group.ExclusiveGroup,
			group.SourceVpk,
			group.Materials.Count == 0 ? null : [.. group.Materials],
			group.Members.Count == 0
				? null
				: [.. group.Members.Select(target => new TargetDto(target.Unit, [.. target.Sections.Order()]))],
			null,
			group.Key);

	private static SectionGroup FromDto(GroupDto dto)
		=> new(
			dto.Name ?? string.Empty,
			dto.SourceVpk ?? string.Empty,
			dto.Materials ?? [],
			[.. (dto.Members ?? dto.Disabled ?? [])
				.Where(target => !string.IsNullOrWhiteSpace(target.Unit))
				.Select(target => new SectionGroupTarget(
					NormalizeUnit(target.Unit!),
					[.. (target.Sections ?? []).Where(section => section >= 0).Distinct().Order()]))])
		{
			Id = dto.Id ?? string.Empty,
			// 旧文件没有这个字段，缺省就是源侧 $no_draw 那一类。
			Source = dto.Source ?? SectionGroupSource.NoDraw,
			ExclusiveGroup = string.IsNullOrWhiteSpace(dto.ExclusiveGroup) ? null : dto.ExclusiveGroup,
			// 旧文件没有稳定键；那种组的名字就是它的键（改名在前一版里本来也保不住）。
			Key = string.IsNullOrWhiteSpace(dto.Key) ? null : dto.Key,
		};

	/// <summary>unit 键归一：去空白、去 <c>0x</c> 前缀、转小写（与 <c>sections.json</c> 同口径）。</summary>
	private static string NormalizeUnit(string unit)
	{
		var text = unit.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			text = text[2..];
		return text.ToLowerInvariant();
	}
}
