using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>一个 VPK 在这次导入里算什么。</summary>
public enum VpkImportKind
{
	/// <summary>含角色模型：要转成 Unit patch。</summary>
	CharacterModel,

	/// <summary>不含模型、只带材质/贴图：要转成材质包。</summary>
	Material,

	/// <summary>不是我们能忠实地翻译的东西（道具、武器、$no_draw 开关…）。</summary>
	Unsupported,
}

/// <summary>
/// 一个模型的几何指纹。
/// <para>
/// <b>为什么要指纹。</b>L4D2 的"8 人替换包"是同一个角色按 8 个 survivor 各导出一份，
/// 8 个 VPK 里的模型<b>几何完全一致</b>（实测 92 材质 / 121511 顶点 / 159363 三角形），
/// 差别只在 mdl 里的骨架名与校验和。逐份转换会得到 8 套一模一样的 Unit，
/// 既浪费又会在部署时互相覆盖——所以必须认出"这是同一个模型"。
/// </para>
/// <para>
/// <b>为什么不用 mdl 的校验和</b>：那个字段每份都不同（骨架名不同就会变），
/// 它区分的是"文件不同"，而我们要的是"模型相同"。
/// </para>
/// </summary>
public sealed record VpkModelSignature(int MaterialCount, int Lod0VertexCount, int TriangleCount)
{
	public override string ToString()
		=> $"{MaterialCount} 材质 / {Lod0VertexCount} 顶点 / {TriangleCount} 三角形";
}

/// <summary>规划结果里的一条：这个 VPK 要转、跳过，或者与别人是同一个模型。</summary>
public sealed record VpkImportCandidate(
	string VpkPath,
	VpkImportKind Kind,
	VpkModelSignature? Signature,
	string? SkipReason,
	string? DuplicateOfVpkPath,
	IReadOnlyCollection<string> OwnedMaterialNames,
	IReadOnlyCollection<string> OwnedTextureNames)
{
	/// <summary>这次要不要真的转换它。</summary>
	public bool IsConvertible => Kind != VpkImportKind.Unsupported && SkipReason is null && DuplicateOfVpkPath is null;
}

/// <summary>一次导入的规划：每个 VPK 的去向与理由。</summary>
public sealed record VpkImportPlan(IReadOnlyList<VpkImportCandidate> Candidates)
{
	public IReadOnlyList<VpkImportCandidate> Convertible
		=> Candidates.Where(candidate => candidate.IsConvertible).ToArray();

	public IReadOnlyList<VpkImportCandidate> Skipped
		=> Candidates.Where(candidate => !candidate.IsConvertible).ToArray();

	public IReadOnlyList<VpkImportCandidate> CharacterModels
		=> Candidates.Where(candidate => candidate.IsConvertible && candidate.Kind == VpkImportKind.CharacterModel).ToArray();

	public IReadOnlyList<VpkImportCandidate> Materials
		=> Candidates.Where(candidate => candidate.IsConvertible && candidate.Kind == VpkImportKind.Material).ToArray();
}

/// <summary>
/// 决定"这批 VPK 里哪些值得转、哪些跳过、哪些是同一个模型"。
/// <para>
/// <b>为什么需要它。</b>一个 L4D2 角色包（实测 60 个 VPK / 357 MB）里通常只有
/// 9 个模型包 + 14 个材质包是可翻译的，剩下的是道具、武器和 <c>$no_draw</c> 开关。
/// 全部硬转既慢又会产出一堆空包，所以先把"能不能转"算清楚，再让转换器只干该干的活。
/// </para>
/// <para><b>三条规则</b></para>
/// <list type="number">
/// <item>含 <c>.mdl</c> 且骨架是 ValveBiped 角色骨架 → 模型；否则（道具/武器）跳过。</item>
/// <item>几何指纹相同的模型包是同一个模型的复制品 → 只转第一份（按路径序），其余合并说明。</item>
/// <item>只有材质的包：它定义的名字必须被某个角色模型（直接或经 <c>$basetexture</c> 等间接）引用，
/// 否则跳过；整包都是 <c>$no_draw</c> 的跳过（HD2 没有"不绘制"这种材质）。</item>
/// </list>
/// <para>
/// <b>健壮身形与纤细身形算同一个模型</b>：指纹只看几何，不看身形；身形是 HD2 外壳的属性，
/// 同一份源模型无论落到哪种身形上都是同一个模型。
/// </para>
/// </summary>
public static class VpkImportPlanner
{
	/// <summary>角色骨架的前缀。重定向的全部依据就是这套骨名。</summary>
	private const string ValveBipedPrefix = "ValveBiped.Bip01_";

	/// <summary>判定"这是角色骨架"至少要有的骨（短名，已剥前缀）。</summary>
	private static readonly string[] RequiredBones = ["Pelvis", "Spine", "Head1", "L_Thigh", "L_UpperArm"];

	public static VpkImportPlan Plan(IReadOnlyList<string> vpkPaths)
	{
		ArgumentNullException.ThrowIfNull(vpkPaths);
		var paths = vpkPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var probes = paths.Select(Probe).ToArray();

		// ② 同一个模型只留一份。代表取路径序第一个——它由"导入时看到的文件名"决定，不是随机的。
		var representativeBySignature = new Dictionary<VpkModelSignature, string>();
		var candidates = new List<VpkImportCandidate>(probes.Length);
		foreach (var probe in probes)
		{
			if (probe.Kind != VpkImportKind.CharacterModel || probe.SkipReason is not null)
			{
				candidates.Add(probe);
				continue;
			}

			var signature = probe.Signature!;
			if (representativeBySignature.TryGetValue(signature, out var representative))
			{
				candidates.Add(probe with
				{
					DuplicateOfVpkPath = representative,
					SkipReason = $"与 {Path.GetFileName(representative)} 是同一个模型（{signature}），只转换一份。",
				});
				continue;
			}

			representativeBySignature[signature] = probe.VpkPath;
			candidates.Add(probe);
		}

		// ③ 材质包：必须被角色模型引用（直接名字或经 VMT 里的贴图键间接引用）。
		var referenced = CollectReferencedNames(candidates, paths);
		for (var index = 0; index < candidates.Count; index++)
		{
			var candidate = candidates[index];
			if (candidate.Kind != VpkImportKind.Material || candidate.SkipReason is not null)
				continue;
			if (candidate.OwnedMaterialNames.Any(referenced.Contains) || candidate.OwnedTextureNames.Any(referenced.Contains))
				continue;
			candidates[index] = candidate with
			{
				SkipReason = "它带的材质/贴图没有被任何角色模型引用（多半是给别的角色或道具用的）。",
			};
		}

		return new VpkImportPlan(candidates);
	}

	/// <summary>
	/// 角色模型"直接用到"的材质名，加上这些材质在 VMT 里引用到的贴图名。
	/// <para>
	/// 间接这一步是必须的：A1 的 <c>white.vmt</c> 指向的 <c>white.vtf</c> 只存在于 D1，
	/// 少了这一步 D1 会被判成"没人引用"而跳过，A1 转出来的材质就会缺贴图。
	/// </para>
	/// </summary>
	private static HashSet<string> CollectReferencedNames(
		IReadOnlyList<VpkImportCandidate> candidates,
		IReadOnlyList<string> vpkPaths)
	{
		var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var models = candidates
			.Where(candidate => candidate.Kind == VpkImportKind.CharacterModel && candidate.DuplicateOfVpkPath is null)
			.ToArray();
		if (models.Length == 0)
			return referenced;

		var source = SourceMaterialSource.Open(vpkPaths);
		var pending = new List<string>();
		foreach (var model in models)
		{
			foreach (var name in model.OwnedMaterialNames)
			{
				// 模型的 OwnedMaterialNames 在这里被复用为"模型引用的材质名"。
				if (referenced.Add(name))
					pending.Add(name);
			}
		}

		// 只展开一层：VMT 引用的是贴图，不会再去引用别的 VMT。
		foreach (var name in pending)
		{
			if (!source.TryReadVmt(name, out var bytes))
				continue;
			VmtDocument document;
			try
			{
				document = VmtDocument.Parse(bytes);
			}
			catch (InvalidDataException)
			{
				continue;
			}

			foreach (var (key, value) in document.Values)
			{
				if (!key.StartsWith('$'))
					continue;
				if (!key.EndsWith("texture", StringComparison.OrdinalIgnoreCase)
					&& !key.EndsWith("map", StringComparison.OrdinalIgnoreCase))
					continue;
				var stem = TextureStem(value);
				if (stem.Length != 0)
					referenced.Add(stem);
			}
		}

		return referenced;
	}

	/// <summary>
	/// 单个 VPK 探测失败时，哪些异常该降级成"跳过"而不是让整批失败。
	/// <para>
	/// <b>为什么这个集合必须够宽。</b>探测的职责是<b>分类</b>，不是保证数据合法：用户丢进来的是
	/// 一整包（实测 60 个 VPK），其中任何<b>一个</b>坏文件都不该让另外 59 个导不进来。
	/// 二进制解析遇到畸形数据时抛什么是实现细节——<see cref="NotSupportedException"/>（版本不认识）、
	/// <see cref="IndexOutOfRangeException"/>（偏移越界）、<see cref="OverflowException"/>（长度字段离谱）
	/// 都是"这个文件不对"，不是"程序有 bug"。理由里带上异常类型，真有 bug 时仍然看得见。
	/// </para>
	/// </summary>
	private static bool IsProbeFailure(Exception exception) => exception is
		IOException or InvalidDataException or NotSupportedException or InvalidOperationException
		or UnauthorizedAccessException or KeyNotFoundException or ArgumentException
		or IndexOutOfRangeException or OverflowException or FormatException;

	private static VpkImportCandidate Probe(string path)
	{
		try
		{
			using var archive = VpkArchive.Open(path);
			var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var hasModel = false;
			foreach (var entry in archive.Entries)
			{
				if (entry.Extension.Equals("mdl", StringComparison.OrdinalIgnoreCase))
					hasModel = true;
				else if (entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
					materials.Add(entry.FileName);
				else if (entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
					textures.Add(entry.FileName);
			}

			if (hasModel)
				return ProbeModel(path, archive, materials, textures);

			if (materials.Count == 0 && textures.Count == 0)
			{
				return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
					"里面既没有 .mdl，也没有 .vmt/.vtf —— 没有可转换的内容。", null, materials, textures);
			}

			// 整包都是 `$no_draw`：源侧靠"不绘制"来隐藏部件，HD2 的材质模板没有这个槽。
			if (materials.Count != 0 && materials.All(name => IsNoDraw(archive, name)))
			{
				return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
					"它是一组 `$no_draw` 材质（源侧靠不绘制来隐藏部件），而 HD2 没有\"不绘制\"这种材质，"
					+ "所以「开关」在 HD2 里没有等价物。", null, materials, textures);
			}

			return new VpkImportCandidate(path, VpkImportKind.Material, null, null, null, materials, textures);
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"打不开（{exception.GetType().Name}）：{exception.Message}", null,
				Array.Empty<string>(), Array.Empty<string>());
		}
	}

	private static VpkImportCandidate ProbeModel(
		string path,
		VpkArchive archive,
		HashSet<string> materials,
		HashSet<string> textures)
	{
		SourceModelFiles files;
		VpkEntry mdl;
		try
		{
			files = SourceModelLocator.Resolve(archive, null);
			mdl = files.Mdl;
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"模型读不出来（{exception.GetType().Name}）：{exception.Message}", null, materials, textures);
		}

		SourceModelDocument model;
		try
		{
			model = SourceModelReader.Read(archive.Read(mdl));
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			// 版本号不认识时 SourceModelReader 抛 NotSupportedException。这是"这个模型不属于
			// L4D2 / Source 2013 那一档"，不是程序出错：必须降级成"跳过这一个包"。
			// 之前它逃到 CLI 的兜底处理器，整批导入直接变成 UnhandledError（实测用户反馈）。
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"模型 {mdl.Path} 读不出来（{exception.GetType().Name}）：{exception.Message}", null, materials, textures);
		}

		if (!IsCharacterRig(model))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"含模型，但骨架不是角色骨架（缺 {string.Join("、", RequiredBones)} 中的骨）——"
				+ "它多半是道具或武器，而这一轮只支持角色模型。", null, materials, textures);
		}

		int lod0Vertices;
		int triangles;
		try
		{
			SourceVertexReader.Read(archive.Read(files.Vvd), out _, out lod0Vertices);
			var parts = SourceIndexReader.Read(archive.Read(files.Vtx), out _, out _);
			// 只数 LOD0：它是"这个模型有多大"的稳定口径，低 LOD 是派生的。
			triangles = parts.Sum(part => part.Models.Sum(model => model.Lods.Count == 0
				? 0
				: model.Lods[0].Meshes.Sum(mesh => mesh.Groups.Sum(group => group.Indices.Count)))) / 3;
		}
		catch (Exception exception) when (IsProbeFailure(exception))
		{
			return new VpkImportCandidate(path, VpkImportKind.Unsupported, null,
				$"几何读不出来（{exception.GetType().Name}）：{exception.Message}", null, materials, textures);
		}

		var signature = new VpkModelSignature(model.MaterialPaths.Count, lod0Vertices, triangles);
		// 模型"引用"的材质名放在 OwnedMaterialNames 里：规划阶段它就是引用集合。
		var referenced = model.MaterialPaths
			.Select(MaterialStem)
			.Where(name => name.Length != 0)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new VpkImportCandidate(path, VpkImportKind.CharacterModel, signature, null, null, referenced, textures);
	}

	private static bool IsCharacterRig(SourceModelDocument model)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var bone in model.Bones)
		{
			var name = bone.Name;
			if (name is null)
				continue;
			if (name.StartsWith(ValveBipedPrefix, StringComparison.OrdinalIgnoreCase))
				names.Add(name[ValveBipedPrefix.Length..]);
		}

		return RequiredBones.All(names.Contains);
	}

	private static bool IsNoDraw(VpkArchive archive, string materialName)
	{
		var entry = archive.Entries.FirstOrDefault(candidate =>
			candidate.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase)
			&& candidate.FileName.Equals(materialName, StringComparison.OrdinalIgnoreCase));
		if (entry is null)
			return false;
		try
		{
			return VmtDocument.Parse(archive.Read(entry)).IsEnabled("$no_draw");
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	/// <summary>MDL 材质表里的裸名：可能带目录、可能带扩展名，统一取最后一段去扩展名。</summary>
	private static string MaterialStem(string path)
		=> Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(path));

	/// <summary>VMT 里贴图键的值：可能带目录、可能带扩展名，统一取最后一段去扩展名。</summary>
	private static string TextureStem(string value)
	{
		var trimmed = value.Trim().Trim('"');
		if (trimmed.Length == 0)
			return string.Empty;
		// 值不是路径（例如 `$matcap 2` 这种数字）时没有意义，别把它当成贴图名。
		if (!trimmed.Contains('/') && !trimmed.Contains('\\') && !trimmed.Contains('.'))
			return string.Empty;
		return Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(trimmed));
	}
}
