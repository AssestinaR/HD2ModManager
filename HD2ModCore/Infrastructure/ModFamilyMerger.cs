using System.IO;
using HD2ModCore.Application;

namespace HD2ModCore.Infrastructure;

/// <summary>同 `idNamespace` 的一个成员：模型或它的材质包。</summary>
/// <param name="DirectoryName">库里的目录名。</param>
/// <param name="DisplayName">库里的显示名（一般等于目录名）。</param>
/// <param name="IsModel">是不是模型主体（有 <c>custom-model.json</c>）。</param>
/// <param name="SourceVpks">它根上的来源 VPK（重构建的输入）。</param>
public sealed record ModFamilyMember(
	string DirectoryName,
	string DisplayName,
	bool IsModel,
	IReadOnlyList<string> SourceVpks);

/// <summary>一个待归拢的家族：一个模型 + 它的材质包。</summary>
public sealed record ModFamily(string IdNamespace, ModFamilyMember Model, IReadOnlyList<ModFamilyMember> Materials);

/// <summary>归拢计划：哪些家族要并、为什么并。</summary>
public sealed record ModFamilyMergePlan(IReadOnlyList<ModFamily> Families, IReadOnlyList<string> Notes)
{
	public static ModFamilyMergePlan Empty { get; } = new(Array.Empty<ModFamily>(), Array.Empty<string>());
}

/// <summary>归拢结果。</summary>
public sealed record ModFamilyMergeOutcome(
	int MergedFamilies,
	int MovedVpks,
	IReadOnlyList<string> Rebuilt,
	IReadOnlyList<string> Notes,
	IReadOnlyList<string> Errors);

/// <summary>
/// 把「旧库里的材质包是独立 Mod」这种形状**归拢成存储约定要求的那一个条目**。
/// <para>
/// <b>做法只有两步，刻意不引入第三个迁移器。</b>
/// </para>
/// <list type="number">
/// <item>把材质包 Mod 根上的来源 VPK **搬进模型 Mod 的根**——根 = 来源，这一层本来就该是平的；</item>
/// <item>对模型 Mod 跑一次**重构建**：它会把根上所有 VPK 一起转，产出
/// <c>sectional/patch_0</c>(unit) + <c>patch_1..N</c>(材质) 的平铺载荷。
/// （转换器天然就是这样：一次调用、每个输入一个 patch 组。）</item>
/// </list>
/// <para>
/// <b>为什么不做别的。</b>以前的迁移器试过"把成员的 patch 搬进宿主 + 重写结构文件"，
/// 结果产生了第二层错误（patch 进错层、角色判错、还要再写一个迁移器去修它）。搬 VPK 是
/// **不可出错**的动作：文件挪个位置，语义完全不变；真正的产物形状由重构建保证，
/// 而重构建本来就必须在新版管理器上跑一次（转换器一直在变）。
/// </para>
/// <para>
/// 搬走 VPK 之后的空壳目录不在这里删：那是「库对账 + 残留清理」已经负责的事
/// （<c>library sync apply</c> / <c>library cleanup</c>），不另写一套删除逻辑。
/// </para>
/// </summary>
public sealed class ModFamilyMerger
{
	private readonly IPatchFileNameParser _parser;

	public ModFamilyMerger(IPatchFileNameParser parser)
		=> _parser = parser ?? throw new ArgumentNullException(nameof(parser));

	/// <summary>
	/// 扫库，找出「一个模型 + ≥1 个材质包，且 idNamespace 相同」的家族。
	/// <para>判据只用两样**现存的事实**：转换报告里的 <c>idNamespace</c>（同一批转出来的凭据）
	/// 与 <c>custom-model.json</c> 在不在（谁是模型）。不猜、不读库快照。</para>
	/// </summary>
	public ModFamilyMergePlan Plan(string modsRootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		if (!Directory.Exists(modsRootDirectory)) return ModFamilyMergePlan.Empty;

		var notes = new List<string>();
		var byNamespace = new Dictionary<string, List<ModFamilyMember>>(StringComparer.Ordinal);
		foreach (var directory in Directory.EnumerateDirectories(modsRootDirectory, "*", SearchOption.TopDirectoryOnly))
		{
			var name = Path.GetFileName(directory);
			if (name.StartsWith(CustomModelCatalog.ManagerDirectoryPrefix, StringComparison.OrdinalIgnoreCase)) continue;

			var report = VpkConversionReport.TryRead(directory);
			if (report?.IdNamespace is not { Length: > 0 } idNamespace) continue;

			var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory);
			var isModel = payload is not null
				&& File.Exists(Path.Combine(payload, VpkCustomModelFile.FileName));
			var sources = Directory
				.EnumerateFiles(directory, "*.vpk", SearchOption.TopDirectoryOnly)
				.Where(path => !IsVolume(path))
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			if (sources.Length == 0) continue;

			if (!byNamespace.TryGetValue(idNamespace, out var members))
				byNamespace[idNamespace] = members = [];
			members.Add(new ModFamilyMember(name, name, isModel, sources));
		}

		var families = new List<ModFamily>();
		foreach (var (idNamespace, members) in byNamespace.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			var models = members.Where(member => member.IsModel).ToArray();
			var materials = members.Where(member => !member.IsModel).ToArray();
			if (models.Length == 0)
			{
				notes.Add($"{idNamespace}：没有模型主体，只有 {materials.Length} 个材质包——跳过（不知道并给谁）。");
				continue;
			}

			if (models.Length > 1)
			{
				notes.Add($"{idNamespace}：有 {models.Length} 个模型主体（{string.Join("、", models.Select(m => m.DirectoryName))}）——"
					+ "跳过，同一个命名空间下出现两个模型本身就不正常，需要人工判断。");
				continue;
			}

			if (materials.Length == 0) continue;   // 已经是一个自洽条目（例如自带材质的模型）

			families.Add(new ModFamily(idNamespace, models[0], materials));
		}

		return new ModFamilyMergePlan(families, notes);
	}

	/// <summary>
	/// 执行归拢：搬 VPK → 重构建。
	/// </summary>
	/// <param name="rebuild">
	/// 实际的重构建动作。由调用方注入：CLI 传 <see cref="VpkRebuildService.RunAsync"/> 的包装，
	/// GUI 传它自己那条（要在后台任务里报进度）。返回 <c>null</c> 表示成功，否则是失败原因。
	/// </param>
	public async Task<ModFamilyMergeOutcome> ApplyAsync(
		string modsRootDirectory,
		ModFamilyMergePlan plan,
		Func<string, string, CancellationToken, ValueTask<string?>> rebuild,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(rebuild);

		var notes = new List<string>();
		var errors = new List<string>();
		var rebuilt = new List<string>();
		var moved = 0;
		var merged = 0;

		foreach (var family in plan.Families)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var modelDirectory = Path.Combine(modsRootDirectory, family.Model.DirectoryName);
			if (!Directory.Exists(modelDirectory))
			{
				errors.Add($"{family.Model.DirectoryName}：模型目录不存在，跳过。");
				continue;
			}

			// ① 搬来源 VPK。同名同大小 = 同一份，直接跳过；同名不同大小 = 拒绝，不猜。
			var failed = false;
			foreach (var material in family.Materials)
			{
				var materialDirectory = Path.Combine(modsRootDirectory, material.DirectoryName);
				foreach (var source in material.SourceVpks)
				{
					var target = Path.Combine(modelDirectory, Path.GetFileName(source));
					if (File.Exists(target))
					{
						if (new FileInfo(target).Length == new FileInfo(source).Length)
						{
							notes.Add($"{material.DirectoryName}：{Path.GetFileName(source)} 与模型里的同名同大小，视为同一份，跳过。");
							continue;
						}

						errors.Add($"{material.DirectoryName}：{Path.GetFileName(source)} 与模型里的同名但大小不同——"
							+ "两份都留着，请人工决定用哪一份。这个家族的归拢已中止。");
						failed = true;
						break;
					}

					try
					{
						VpkPayloadSwap.ClearReadOnly(source);
						File.Move(source, target);
						moved++;
					}
					catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
					{
						errors.Add($"{material.DirectoryName}：搬 {Path.GetFileName(source)} 失败：{exception.Message}");
						failed = true;
						break;
					}
				}

				if (failed) break;
			}

			if (failed) continue;

			// ② 重构建：根上有几个 VPK，就转出几组 patch，产物平铺进 sectional/。
			var reason = await rebuild(family.Model.DirectoryName, modelDirectory, cancellationToken)
				.ConfigureAwait(false);
			if (reason is not null)
			{
				errors.Add($"{family.Model.DirectoryName}：VPK 已搬好，但重构建失败——{reason}");
				continue;
			}

			rebuilt.Add(family.Model.DirectoryName);
			merged++;
			notes.Add($"{family.Model.DirectoryName}：并进 {family.Materials.Count} 个材质包、"
				+ $"{family.Materials.Sum(item => item.SourceVpks.Count)} 个 VPK，已重构建。");
		}

		return new ModFamilyMergeOutcome(merged, moved, rebuilt, notes, errors);
	}

	/// <summary>纯数字分卷（<c>xxx_001.vpk</c>）：由 <c>_dir.vpk</c> 引用，单列出来会被重复转换。</summary>
	private static bool IsVolume(string path)
	{
		var stem = Path.GetFileNameWithoutExtension(path);
		return stem.Length > 4 && stem[^4] == '_' && stem[^3..].All(char.IsAsciiDigit);
	}
}
