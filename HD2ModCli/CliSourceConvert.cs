using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;

namespace HD2ModCli;

// 作用：`source convert` 的外壳发现与输出格式化。
// 外壳**不写死存档 ID**：从游戏自己的装备权威表里按部位找，这样换版本、换护甲都不用改代码。
// Purpose: discovers structural donors from the game's own equipment authority table.
internal static class CliSourceConvert
{
	/// <summary>部位 → 游戏装备表里的 partKind。与 <c>UnitMeshPartKind</c> 的枚举名一致。</summary>
	internal static readonly IReadOnlyDictionary<BodyRegion, string> PartKinds = new Dictionary<BodyRegion, string>
	{
		[BodyRegion.Head] = "Head",
		[BodyRegion.Chest] = "Torso",
		[BodyRegion.Hips] = "Pelvis",
		[BodyRegion.LeftLeg] = "LeftLeg",
		[BodyRegion.RightLeg] = "RightLeg",
		[BodyRegion.LeftArm] = "LeftArm",
		[BodyRegion.RightArm] = "RightArm",
	};

	/// <summary>部位 → 写进 Unit 的槽位名（<c>HelldiverCustomizationSlot_</c> 之后的部分）。</summary>
	internal static readonly IReadOnlyDictionary<BodyRegion, string> SlotNames = new Dictionary<BodyRegion, string>
	{
		[BodyRegion.Head] = "Helmet",
		[BodyRegion.Chest] = "Torso",
		[BodyRegion.Hips] = "Hip",
		[BodyRegion.LeftLeg] = "LeftLeg",
		[BodyRegion.RightLeg] = "RightLeg",
		[BodyRegion.LeftArm] = "LeftArm",
		[BodyRegion.RightArm] = "RightArm",
	};

	internal sealed record DonorSelection(string PartKind, string ArchiveId, ulong FileId, string BodyVariant, uint Slot);

	/// <summary>
	/// 每个部位给出<b>按偏好排序的候选外壳</b>。优先"Any"身形（与身形无关），其次"Stocky"（HD2 原生素体就是 Stocky）。
	/// 返回候选表而不是单选，是因为同一个部位在游戏里有多个 Unit，其中一些<b>没有槽位记录</b>
	/// （<c>CustomizationInfoOffset = 0</c>），而我们的槽位改写必须先有那条记录。
	/// 挑不到的部位<b>不猜</b>：直接报出来。
	/// </summary>
	internal static (IReadOnlyDictionary<BodyRegion, IReadOnlyList<DonorSelection>> Candidates, IReadOnlyList<string> Missing) SelectDonors(
		IReadOnlyList<CliEquipmentFactKit> kits)
	{
		var byPartKind = kits
			.SelectMany(kit => kit.Units.Select(unit => (kit, unit)))
			.GroupBy(item => item.unit.PartKind, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

		var candidates = new Dictionary<BodyRegion, IReadOnlyList<DonorSelection>>();
		var missing = new List<string>();
		foreach (var (region, partKind) in PartKinds)
		{
			if (!byPartKind.TryGetValue(partKind, out var found))
			{
				missing.Add($"{region.Name()}（partKind={partKind}）");
				continue;
			}

			var ordered = found
				.OrderBy(item => VariantRank(item.unit.BodyVariant))
				.ThenBy(item => item.kit.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.ThenBy(item => item.unit.FileId, StringComparer.Ordinal)
				.Select(item => ToSelection(region, item.kit, item.unit))
				.Where(selection => selection is not null)
				.Select(selection => selection!)
				.ToArray();
			if (ordered.Length == 0)
			{
				missing.Add($"{region.Name()}（候选的 fileId 都解析失败）");
				continue;
			}

			candidates[region] = ordered;
		}

		return (candidates, missing);
	}

	/// <summary>身形偏好：Any 最好（与身形无关），其次 Stocky，最后其它。</summary>
	private static int VariantRank(string bodyVariant) => bodyVariant.ToUpperInvariant() switch
	{
		"ANY" => 0,
		"STOCKY" => 1,
		"SLIM" => 2,
		_ => 3,
	};

	private static DonorSelection? ToSelection(BodyRegion region, CliEquipmentFactKit kit, CliEquipmentFactUnit unit)
	{
		var fileId = unit.FileId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? unit.FileId[2..] : unit.FileId;
		return ulong.TryParse(fileId, System.Globalization.NumberStyles.HexNumber, null, out var parsed)
			? new DonorSelection(PartKinds[region], kit.ArchiveId, parsed, unit.BodyVariant, unit.Slot)
			: null;
	}

	/// <summary>
	/// 把候选读成 <see cref="UnitDonor"/>，每个部位取<b>第一个带槽位记录的</b>。
	/// 没有槽位记录的外壳没法被改写槽位，用它会让所有 Unit 挤在同一个槽上——那正是要避免的。
	/// </summary>
	internal static async ValueTask<(IReadOnlyDictionary<BodyRegion, UnitDonor> Donors, IReadOnlyList<string> Failures)> LoadDonorsAsync(
		string gameDataDirectory,
		IReadOnlyDictionary<BodyRegion, IReadOnlyList<DonorSelection>> candidates,
		CancellationToken cancellationToken)
	{
		var resolver = new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(gameDataDirectory);
		var reader = new GameDataUnitMeshReader(resolver);
		var donors = new Dictionary<BodyRegion, UnitDonor>();
		var failures = new List<string>();
		foreach (var (region, options) in candidates)
		{
			var reasons = new List<string>();
			foreach (var selection in options)
			{
				try
				{
					var mesh = await reader.ReadAsync(
						selection.ArchiveId,
						new HD2ModAdaptation.PatchReconstruction.AssetKey(PatchUnitMeshReader.UnitTypeId, selection.FileId),
						dependencyArchiveNames: null,
						allowGlobalDependencySearch: true,
						cancellationToken).ConfigureAwait(false);
					if (!mesh.Model.CustomizationInfo.HasValue || mesh.Model.CustomizationInfoOffset == 0)
					{
						reasons.Add($"0x{selection.FileId:x16} 没有槽位记录");
						continue;
					}

					donors[region] = new UnitDonor(
						selection.ArchiveId,
						selection.FileId,
						mesh.Model,
						mesh.Payload.TocData,
						mesh.Payload.Entry.Unknown1,
						mesh.Payload.Entry.Unknown2,
						mesh.Payload.Entry.Unknown3,
						mesh.Payload.Entry.Unknown4);
					break;
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or FileNotFoundException)
				{
					reasons.Add($"0x{selection.FileId:x16} 读取失败：{exception.Message}");
				}
			}

			if (!donors.ContainsKey(region))
				failures.Add($"{region.Name()}：{string.Join("；", reasons.Take(3))}");
		}

		return (donors, failures);
	}

	/// <summary>人类可读输出。逐 Unit 报顶点/三角形/体积，最后给一句"这一版贴图是原版护甲的"提醒。</summary>
	internal static string Format(
		string mdlPath,
		string outputDirectory,
		int groupCount,
		int selectedCount,
		VpkPackageResult package,
		string modelKey,
		string modelName,
		string? registryError)
	{
		var lines = new List<string>
		{
			$"转换：{mdlPath}",
			$"  切分 {groupCount} 份，本次写出 {selectedCount} 个 Unit → {outputDirectory}",
		};

		foreach (var unit in package.Units)
		{
			var status = unit.IsValid ? string.Empty : $"  失败：{string.Join("；", unit.Diagnostics.Take(2))}";
			var pieces = string.Join("+", unit.RegistryPieces.Select(piece =>
				$"{piece.Slot}/{piece.PieceType}{(piece.Body == 3 ? "Any" : piece.Body.ToString())}"));
			lines.Add($"  0x{unit.UnitId}  {unit.Name,-32} v={unit.VertexCount,-7} tri={unit.TriangleCount,-7} "
				+ $"注册={pieces,-14} toc={unit.TocBytes,-7} gpu={unit.GpuBytes / 1024,-6} KiB{status}");
		}

		foreach (var diagnostic in package.Diagnostics)
			lines.Add($"  诊断：{diagnostic}");

		lines.Add(package.TocPath is null ? "未写出 Patch。" : $"已写出：{package.TocPath}");

		if (registryError is null)
		{
			lines.Add($"已登记素体：{modelName}（{modelKey}）→ 写入管理器工作区；"
				+ "请到管理器「护甲映射」页应用一次，由管理器自己编译成注册协议。");
		}
		else
		{
			lines.Add($"⚠️ 素体<b>未登记</b>：{registryError}");
			lines.Add("   结果目录里留有 custom-model.json，可以在管理器里手工登记。");
		}

		// 这一条必须说清楚，否则上机会把"贴图不对"误当成几何有问题。
		lines.Add("注意：外壳带的是游戏本体护甲的材质，所以这一版**形状对、贴图是原版护甲的**——材质是 M3 的事。");
		return string.Join(Environment.NewLine, lines);
	}
}
