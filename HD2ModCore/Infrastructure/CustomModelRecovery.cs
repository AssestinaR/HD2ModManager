using System.IO;
using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <param name="Recovered">补出 <c>custom-model.json</c> 的 Mod 名（沿用旧 key 的会标出来）。</param>
/// <param name="ReusedKeys">其中有多少个是"按 unit id 找回原 key"的。</param>
/// <param name="Failed">补失败的（Mod 名 + 原因）。</param>
public sealed record CustomModelRecoveryReport(
	IReadOnlyList<string> Recovered,
	int ReusedKeys,
	IReadOnlyList<string> Failed)
{
	public static readonly CustomModelRecoveryReport Empty = new([], 0, []);

	public bool IsEmpty => Recovered.Count == 0 && Failed.Count == 0;

	public string? Describe()
	{
		if (Recovered.Count == 0 && Failed.Count == 0) return null;
		var parts = new List<string>();
		if (Recovered.Count != 0)
			parts.Add($"已为 {Recovered.Count} 个 Mod 补出自定义素体标记"
				+ (ReusedKeys > 0 ? $"（其中 {ReusedKeys} 个按 unit id 找回了原 key，映射不会断）" : string.Empty));
		if (Failed.Count != 0)
			parts.Add($"{Failed.Count} 个补失败：{string.Join("；", Failed.Take(3))}");
		return string.Join("。", parts) + "。";
	}
}

/// <summary>
/// 给"有 Unit 产物、但没有 <c>custom-model.json</c>"的 Mod <b>补出素体标记</b>。
/// <para>
/// <b>为什么需要它。</b>素体的权威来源是 Mod 目录里那份文件（见 <see cref="CustomModelCatalog"/>），
/// 于是"文件不在"就等于"素体不存在"。可文件确实会丢：旧版本导入没写过、载荷替换的边缘情况、
/// 用户手工清理。没有这一步，一次丢失就是永久的 —— 用户只能重新导入整包，映射全得重配。
/// </para>
/// <para>
/// <b>key 必须优先找回，而不是新铸。</b><c>custom-model.json</c> 的 <c>key</c> 是随机 GUID，
/// 从产物反推不出来；而映射记录引用的正是这个 key。只补文件、换一个新 key，旧映射照样对不上，
/// 等于没恢复。所以先用产物里的 <b>unit id</b>（全局唯一、不随 key 变）去工作区里按件表反查，
/// 命中就<b>沿用那个 key 与那个名字</b>；反查不到才新铸。
/// </para>
/// <para>
/// <b>事实来自产物，不靠猜。</b>件表的 (unit, slot, pieceType, body) 全部取自
/// <c>vpk2patch/sections.json</c> 的 <c>units[]</c>；<c>tone</c> / <c>isReserved</c> /
/// <c>originalBody</c> 这类编辑性字段用默认值，并在日志里说明"由产物重建"。
/// 没有 Unit 的产物（纯材质包）直接跳过 —— 给它造一具空素体只会污染列表。
/// </para>
/// </summary>
public sealed class CustomModelRecovery
{
	private const string SectionsFileName = "sections.json";

	private readonly RuntimeArmorRegistryService registry;
	private readonly string modsDirectory;

	public CustomModelRecovery(RuntimeArmorRegistryService registry, string modsDirectory)
	{
		this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
		this.modsDirectory = modsDirectory ?? string.Empty;
	}

	public async ValueTask<CustomModelRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
			return CustomModelRecoveryReport.Empty;

		string[] directories;
		try
		{
			directories = Directory.GetDirectories(modsDirectory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return CustomModelRecoveryReport.Empty;
		}

		// 已有的 key 与件表：用来按 unit id 反查"这具素体原本叫什么 key"。
		var known = await ReadKnownModelsAsync(cancellationToken).ConfigureAwait(false);
		var recovered = new List<string>();
		var failed = new List<string>();
		var reused = 0;
		foreach (var modDirectory in directories
			.OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(modDirectory);
			if (name.StartsWith(CustomModelCatalog.ManagerDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
				continue;
			if (HasDefinition(modDirectory)) continue;

			try
			{
				var pieces = ReadPieces(modDirectory);
				if (pieces.Count == 0) continue;   // 没有 Unit 的产物（材质包）：跳过，不造空素体。

				var units = pieces.Select(piece => piece.Unit).ToHashSet(StringComparer.OrdinalIgnoreCase);
				var owner = known.FirstOrDefault(entry => entry.Units.Overlaps(units));
				var key = owner?.Key ?? "custom-" + Guid.NewGuid().ToString("N");
				if (owner is not null) reused++;
				var modelName = owner?.Name is { Length: > 0 } existing ? existing : name;

				Write(modDirectory, new RuntimeArmorRegistryModel(key, owner?.SourceItemId, pieces, modelName));
				recovered.Add(name);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
				or InvalidDataException or JsonException)
			{
				failed.Add($"{name}（{exception.Message}）");
			}
		}

		return new CustomModelRecoveryReport(recovered, reused, failed);
	}

	private static bool HasDefinition(string modDirectory)
		=> File.Exists(Path.Combine(modDirectory, VpkCustomModelFile.FileName))
			|| File.Exists(Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, VpkCustomModelFile.FileName));

	/// <summary>
	/// 从产物的 <c>sections.json</c> 读件表。两个位置都看：转换产物写在 <c>vpk2patch/</c> 下，
	/// 拆件产物（sectional）可能写在根上。
	/// </summary>
	private static IReadOnlyList<RuntimeArmorRegistryModelPiece> ReadPieces(string modDirectory)
	{
		var candidates = new[]
		{
			Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName, SectionsFileName),
			Path.Combine(modDirectory, SectionsFileName),
		};
		var sectionsPath = candidates.FirstOrDefault(File.Exists);
		if (sectionsPath is null) return [];

		using var stream = File.OpenRead(sectionsPath);
		using var document = JsonDocument.Parse(stream);
		if (!document.RootElement.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
			return [];

		var pieces = new List<RuntimeArmorRegistryModelPiece>();
		foreach (var unit in units.EnumerateArray())
		{
			if (unit.ValueKind != JsonValueKind.Object) continue;
			var fileId = unit.TryGetProperty("fileId", out var fileIdElement) && fileIdElement.ValueKind == JsonValueKind.String
				? fileIdElement.GetString()
				: null;
			if (string.IsNullOrWhiteSpace(fileId)) continue;
			pieces.Add(new RuntimeArmorRegistryModelPiece(
				BodyCode(unit),
				fileId!,
				ReadUInt32(unit, "slot"),
				ReadUInt32(unit, "pieceType")));
		}

		return pieces;
	}

	/// <summary>
	/// <c>sections.json</c> 写的是 <c>bodyType</c> 名字，注册表用的是原始编码
	/// （0=Stocky、1=Slim、3=Any，见 <see cref="RuntimeArmorRegistryModelPiece"/>）。
	/// 认不出的按 Any 处理：Any 是"两种身形通用"，猜错它只会少一个特化件，不会漏画。
	/// </summary>
	private static uint BodyCode(JsonElement unit)
	{
		var text = unit.TryGetProperty("bodyType", out var element) && element.ValueKind == JsonValueKind.String
			? element.GetString()
			: null;
		return text?.Trim().ToLowerInvariant() switch
		{
			"stocky" => 0u,
			"slim" => 1u,
			_ => 3u,
		};
	}

	private static uint ReadUInt32(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetUInt32(out var parsed)
				? parsed
				: 0u;

	private sealed record KnownModel(string Key, string? Name, uint? SourceItemId, HashSet<string> Units);

	private async ValueTask<IReadOnlyList<KnownModel>> ReadKnownModelsAsync(CancellationToken cancellationToken)
	{
		// 工作区里那份（含磁盘上已不存在的历史素体）是"key → 件表"的唯一线索。
		var snapshot = await registry.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return snapshot.Document.ModelDefinitions
			.Select(model => new KnownModel(
				model.Key,
				model.Name,
				model.SourceItemId,
				(model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
					.Select(piece => piece.Unit)
					.Where(unit => !string.IsNullOrWhiteSpace(unit))
					.ToHashSet(StringComparer.OrdinalIgnoreCase)))
			.Where(model => model.Units.Count != 0)
			.ToArray();
	}

	private static void Write(string modDirectory, RuntimeArmorRegistryModel model)
	{
		// 写在 `vpk2patch/` 下：与转换产物一致，且换载荷时会被 PreservedFileNames 保留。
		var payload = Path.Combine(modDirectory, ModNodePayloadLayout.VpkPatchDirectoryName);
		var directory = Directory.Exists(payload) ? payload : modDirectory;
		var path = Path.Combine(directory, VpkCustomModelFile.FileName);
		if (File.Exists(path)) return;   // 幂等：绝不覆盖。

		// 库里的产物文件被标成只读；这里只新建文件，但如果目录里已有同名只读文件，
		// 上面那条 File.Exists 已经挡掉了。仍然先清属性，避免"新建即失败"。
		if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
		var json = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web)
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});
		File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
	}
}
