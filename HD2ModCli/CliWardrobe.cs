using System.Text;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

// `hd2mod wardrobe ...`：衣柜 section 掩码的构建期工具链。
//
// 三条命令对应"编辑 → 编译 → 校验"这条链：
//   sections  从 patch 现算映射表（身份 + 各级落点），只读输入、只写输出文件；
//   compile   把规格文件（人写）编译成 `wardrobe.masks`（运行时读），指纹全部来自映射表；
//   validate  反过来问"这份掩码还跟这份构建产物对得上吗"——重建之后必跑。
//
// 为什么不做成一条命令：映射表是**构建产物**（跟着 mod 走、可重新生成），掩码是**部署产物**
// （跟着目标走、要原子替换），两者的生命周期不同。合成一条会逼着用户每改一个勾选就重算一次
// 整份 patch 的 section 表。
//
// The build-time half of the wardrobe toolchain: sections -> compile -> validate.
// The section map is a build artefact that travels with the mod; the mask is a deployment
// artefact that travels with the target. Their lifetimes differ, so they stay separate
// commands rather than one that re-walks the whole patch on every checkbox.

internal static class CliWardrobe
{
	internal const string SectionsCommand = "wardrobe sections";
	internal const string CompileCommand = "wardrobe compile";
	internal const string ValidateCommand = "wardrobe validate";
	internal const string ProjectCommand = "wardrobe project";

	/// <summary>`wardrobe sections &lt;patch&gt; --output &lt;sections.json&gt;`。</summary>
	internal static async Task<int> RunSectionsAsync(CliInvocation invocation, CancellationToken cancellationToken)
	{
		var options = invocation.Options;
		EnsureOptions(options, SectionsCommand, "--output", "--overwrite");
		var output = RequireOutput(options, SectionsCommand);
		var patchPath = RequirePositional(invocation, SectionsCommand);
		var result = await WardrobeSectionMapFile.BuildAsync(patchPath, cancellationToken).ConfigureAwait(false);
		await CliOutput.WriteExplicitFileAsync(output, WardrobeSectionMapFile.Serialize(result.Document), options.Overwrite, cancellationToken)
			.ConfigureAwait(false);

		var fullPath = Path.GetFullPath(output);
		var document = result.Document;
		var inconsistent = document.Units.Where(unit => !unit.LodConsistent).Select(unit => unit.FileId).ToArray();
		var summary = new
		{
			schema = document.Schema,
			patch = document.Patch,
			unitCount = document.Units.Count,
			sectionCount = document.TotalSectionCount,
			lodInconsistentUnits = inconsistent,
			skipped = result.Skipped.Select(item => new { fileId = item.FileId, reason = item.Reason }).ToArray(),
		};
		Emit(
			options,
			new { ok = true, output = fullPath, sections = summary },
			() =>
			{
				var builder = new StringBuilder();
				builder.Append($"已导出 section 映射表：{fullPath}\n");
				builder.Append($"  {document.Units.Count} 个 unit / {document.TotalSectionCount} 个 section");
				builder.Append($"，patch sha256={document.Patch.Sha256[..16]}…（{document.Patch.Size} 字节）\n");
				if (document.Patch.GpuResourcesSha256 is null)
					builder.Append("  注意：没有找到 .gpu_resources 附属文件，gpuResourcesSha256 留空。\n");
				if (inconsistent.Length > 0)
					builder.Append($"  注意：{inconsistent.Length} 个 unit 的各级 LOD 节数不一致（{string.Join(", ", inconsistent)}）；这些 unit 只保留与 LOD0 节数相同的 LOD。\n");
				foreach (var item in result.Skipped)
					builder.Append($"  跳过 {item.FileId}：{item.Reason}\n");
				return builder.ToString().TrimEnd('\n');
			});
		return 0;
	}

	/// <summary>`wardrobe compile &lt;spec.json&gt; --output &lt;wardrobe.masks&gt;`。</summary>
	internal static async Task<int> RunCompileAsync(CliInvocation invocation, CancellationToken cancellationToken)
	{
		var options = invocation.Options;
		EnsureOptions(options, CompileCommand, "--output", "--overwrite", "--sections");
		var output = RequireOutput(options, CompileCommand);
		var specPath = Path.GetFullPath(RequirePositional(invocation, CompileCommand));
		if (!File.Exists(specPath))
			throw new CliUserException("WardrobeSpecNotFound", $"找不到规格文件：{specPath}");

		var spec = WardrobeMaskCompiler.Parse(await File.ReadAllTextAsync(specPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
		var sectionsPath = ResolveSectionsPath(options.Sections, spec.Sections, specPath);
		var sections = await ReadSingleMapAsync(sectionsPath, CompileCommand, cancellationToken).ConfigureAwait(false);
		var result = WardrobeMaskCompiler.Compile(spec, sections);
		if (result.IsValid)
		{
			await CliOutput.WriteExplicitFileAsync(output, WardrobeMaskFile.Serialize(result.Document), options.Overwrite, cancellationToken)
				.ConfigureAwait(false);
		}

		var fullPath = Path.GetFullPath(output);
		var projection = new
		{
			output = fullPath,
			sectionsPath,
			revision = result.Document.Revision,
			enabled = result.Document.Enabled,
			setCount = result.SetCount,
			entryCount = result.EntryCount,
			targetCount = result.TargetCount,
			diagnostics = result.Diagnostics,
		};
		Emit(
			options,
			new { ok = result.IsValid, compiled = projection },
			() =>
			{
				if (!result.IsValid)
					return $"编译失败，未写出文件：\n{WardrobeMaskCompiler.Describe(result.Diagnostics)}";
				var builder = new StringBuilder();
				builder.Append($"已编译衣柜掩码：{fullPath}\n");
				builder.Append($"  revision={result.Document.Revision} enabled={(result.Document.Enabled ? "true" : "false")}");
				builder.Append($" 掩码集 {result.SetCount} / 条目 {result.EntryCount} / 目标 {result.TargetCount}\n");
				builder.Append($"  依据：{sectionsPath}（patch sha256={sections.Patch.Sha256[..16]}…）");
				if (result.Diagnostics.Count > 0)
					builder.Append($"\n{WardrobeMaskCompiler.Describe(result.Diagnostics)}");
				return builder.ToString();
			});
		return result.IsValid ? 0 : 2;
	}

	/// <summary>`wardrobe validate --file &lt;wardrobe.masks&gt; --sections &lt;sections.json&gt;`。</summary>
	internal static async Task<int> RunValidateAsync(CliInvocation invocation, CancellationToken cancellationToken)
	{
		var options = invocation.Options;
		EnsureOptions(options, ValidateCommand, "--file", "--sections");
		if (string.IsNullOrWhiteSpace(options.File))
			throw new CliUserException("WardrobeMaskRequired", "请通过 --file <wardrobe.masks> 提供要校验的掩码文件。");
		if (string.IsNullOrWhiteSpace(options.Sections))
			throw new CliUserException("WardrobeSectionsRequired", "请通过 --sections <sections.json> 提供构建期 section 映射表。");

		var maskPath = Path.GetFullPath(options.File);
		var sectionsPath = Path.GetFullPath(options.Sections);
		// 掩码文件不存在时按"空文档"处理，与运行时的语义一致：不存在 = 全启用。
		// 这样"刚部署还没配过"的状态能通过校验，而不是报一个找不到文件的错。
		var masks = WardrobeMaskFile.Read(maskPath);
		var index = await ReadSectionIndexAsync(sectionsPath, cancellationToken).ConfigureAwait(false);
		var validation = WardrobeMaskCompiler.Validate(masks, index);
		Emit(
			options,
			new
			{
				ok = validation.IsValid,
				validation = new
				{
					maskPath,
					sectionsPath,
					sectionMapCount = index.SourcePaths.Count,
					sectionMapPaths = index.SourcePaths,
					maskExists = File.Exists(maskPath),
					isValid = validation.IsValid,
					revision = masks.Revision,
					enabled = masks.Enabled,
					entryCount = validation.EntryCount,
					resolvedEntryCount = validation.ResolvedEntryCount,
					setCount = validation.SetCount,
					targetCount = validation.TargetCount,
					targets = validation.Targets,
					diagnostics = validation.Diagnostics,
				},
			},
			() =>
			{
				var builder = new StringBuilder();
				builder.Append($"掩码：{maskPath}{(File.Exists(maskPath) ? string.Empty : "（不存在，视为全启用）")}\n");
				builder.Append($"  映射表：{index.SourcePaths.Count} 份（{sectionsPath}）\n");
				builder.Append($"  revision={masks.Revision} enabled={(masks.Enabled ? "true" : "false")}");
				builder.Append($" 条目 {validation.ResolvedEntryCount}/{validation.EntryCount} 条可解析");
				builder.Append($" 掩码集 {validation.SetCount} / 目标 {validation.TargetCount}\n");
				foreach (var target in validation.Targets)
				{
					builder.Append($"  目标 {target.ItemId} → 掩码集 {target.SetId}：隐藏 {target.HiddenSections} 个 section");
					if (target.FullyHiddenUnits.Count > 0)
						builder.Append($"；整块关闭的 unit：{string.Join(", ", target.FullyHiddenUnits)}");
					builder.Append('\n');
				}

				builder.Append(validation.IsValid
					? "结论：掩码与当前构建产物一致。"
					: "结论：掩码与当前构建产物**不一致**，请重新编译。");
				if (validation.Diagnostics.Count > 0)
					builder.Append('\n').Append(WardrobeMaskCompiler.Describe(validation.Diagnostics));
				return builder.ToString();
			});
		return validation.IsValid ? 0 : 2;
	}

	// --- 选项处理 -----------------------------------------------------------

	/// <summary>`wardrobe project --library &lt;mod库&gt; [--registry-root &lt;协议目录&gt;] --output &lt;wardrobe.masks&gt;`。</summary>
	internal static async Task<int> RunProjectAsync(CliInvocation invocation, CancellationToken cancellationToken)
	{
		var options = invocation.Options;
		EnsureOptions(options, ProjectCommand, "--library", "--registry-root", "--output", "--overwrite", "--sections");
		var output = RequireOutput(options, ProjectCommand);
		if (string.IsNullOrWhiteSpace(options.Library))
		{
			throw new CliUserException(
				"WardrobeLibraryRequired",
				"请通过 --library <Mod库目录> 指定 mod 库；权威数据在 <mod库>/.hd2modmanager/wardrobe.workspace.json。");
		}

		var workspacePath = WardrobeWorkspaceFile.ResolvePath(options.Library);
		WardrobeWorkspaceDocument workspace;
		try
		{
			workspace = WardrobeWorkspaceFile.Read(workspacePath);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException)
		{
			throw new CliUserException("WardrobeWorkspaceInvalid", exception.Message);
		}
		var registryRoot = CliArmorRegistryProtocol.ResolveRegistryRoot(options.RegistryRoot);
		var protocol = await CliArmorRegistryProtocol.ReadProtocolAsync(registryRoot, allowMissing: false).ConfigureAwait(false);
		// 只有"真映射"参与：模型映射（换模型而不是换视觉来源）不提供 section 语义。
		var mappings = protocol.Mappings
			.Where(mapping => !mapping.IsModelMapping && mapping.SourceItemId is not null)
			.Select(mapping => new WardrobeMapping(mapping.TargetItemId, mapping.SourceItemId!.Value))
			.ToArray();

		// 缺省就在 mod 库里递归找 sections.json：映射表随 mod 走（§11），
		// 用户不该为此记住每个 mod 的子目录名。
		var sectionsPath = string.IsNullOrWhiteSpace(options.Sections)
			? Path.GetFullPath(options.Library)
			: Path.GetFullPath(options.Sections);
		var index = await ReadSectionIndexAsync(sectionsPath, cancellationToken).ConfigureAwait(false);
		var result = WardrobeMaskProjection.Project(workspace, mappings, index);
		if (result.IsValid)
		{
			await CliOutput.WriteExplicitFileAsync(output, WardrobeMaskFile.Serialize(result.Document), options.Overwrite, cancellationToken)
				.ConfigureAwait(false);
		}

		var fullPath = Path.GetFullPath(output);
		var projection = new
		{
			output = fullPath,
			workspacePath,
			workspaceExists = File.Exists(workspacePath),
			workspaceRevision = workspace.Revision,
			protocolPath = Path.Combine(registryRoot, CliArmorRegistryProtocol.ProtocolFileName),
			mappingCount = mappings.Length,
			sectionMapCount = index.SourcePaths.Count,
			sectionMapPaths = index.SourcePaths,
			revision = result.Document.Revision,
			enabled = result.Document.Enabled,
			setCount = result.Sets.Count,
			entryCount = result.Document.Entries.Count,
			targetCount = result.Document.TargetSets.Count,
			skippedEntryCount = result.SkippedEntryCount,
			sets = result.Sets,
			diagnostics = result.Diagnostics,
		};
		Emit(
			options,
			new { ok = result.IsValid, projected = projection },
			() =>
			{
				if (!result.IsValid)
					return $"投影失败，未写出文件：\n{WardrobeMaskCompiler.Describe(result.Diagnostics)}";
				var builder = new StringBuilder();
				builder.Append($"已生成衣柜掩码：{fullPath}\n");
				builder.Append($"  revision={result.Document.Revision} enabled={(result.Document.Enabled ? "true" : "false")}");
				builder.Append($" 掩码集 {result.Sets.Count} / 条目 {result.Document.Entries.Count} / 目标 {result.Document.TargetSets.Count}\n");
				builder.Append($"  依据：{workspacePath}{(File.Exists(workspacePath) ? string.Empty : "（不存在，视为全部继承）")}，");
				builder.Append($"{mappings.Length} 条映射，{index.SourcePaths.Count} 份 section 映射表\n");
				foreach (var set in result.Sets)
				{
					builder.Append($"  掩码集 {set.SetId} ← 护甲 {set.OwnerItemId}（");
					builder.Append(set.Origin == WardrobeSetOrigin.Source ? "视觉来源" : "独立设置");
					builder.Append($"，{set.EntryCount} 条）");
					if (set.InheritorItemIds.Count > 0)
						builder.Append($"；继承者：{string.Join(", ", set.InheritorItemIds)}");
					builder.Append('\n');
				}

				if (result.SkippedEntryCount > 0)
					builder.Append($"  跳过 {result.SkippedEntryCount} 条（见下方警告）\n");
				// 这一步很重要：掩码只在**协议被重新应用**时才会被运行时读取，
				// 单改掩码不会触发重载（见 armor_registry_runtime.lua 的 prepare_section_masks）。
				builder.Append("  注意：运行时只在协议应用时读取掩码，改完掩码需要再触发一次协议应用才生效。");
				if (result.Diagnostics.Count > 0)
					builder.Append('\n').Append(WardrobeMaskCompiler.Describe(result.Diagnostics));
				return builder.ToString();
			});
		return result.IsValid ? 0 : 2;
	}

	// --- 选项处理 -----------------------------------------------------------

	/// <summary>
	/// 读单份映射表。<c>--sections</c> 允许给目录，但这条链路需要**恰好一份**：
	/// 规格里钉死了 patch 指纹，校验也按"一份构建产物"回答，多份就没有唯一答案。
	/// </summary>
	private static async ValueTask<WardrobeSectionMapDocument> ReadSingleMapAsync(
		string path,
		string command,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<string> paths;
		try
		{
			paths = WardrobeSectionMapFile.ResolvePaths(path);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
		{
			throw new CliUserException("WardrobeSectionMapInvalid", exception.Message);
		}

		if (paths.Count != 1)
		{
			throw new CliUserException(
				"WardrobeSectionMapAmbiguous",
				$"{command} 需要恰好一份 sections.json，{path} 下找到了 {paths.Count} 份：" +
				$"{string.Join("、", paths)}。跨 mod 的场景请改用 wardrobe project（它按 unit 合并多份表）。");
		}

		try
		{
			return await WardrobeSectionMapFile.ReadAsync(paths[0], cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException)
		{
			throw new CliUserException("WardrobeSectionMapInvalid", exception.Message);
		}
	}

	/// <summary>读一份或多份映射表并合并成索引（文件或目录都行）。</summary>
	private static async ValueTask<WardrobeSectionIndex> ReadSectionIndexAsync(string path, CancellationToken cancellationToken)
	{
		IReadOnlyList<WardrobeSectionMapDocument> documents;
		try
		{
			documents = await WardrobeSectionMapFile.ReadManyAsync(path, cancellationToken).ConfigureAwait(false);
			return WardrobeSectionIndex.Create(documents);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or IOException)
		{
			// 输入路径问题不该显示成 UnhandledError：用户少传一个 --sections 不是程序崩溃。
			throw new CliUserException("WardrobeSectionMapInvalid", exception.Message);
		}
	}

	private static string RequirePositional(CliInvocation invocation, string command)
	{
		if (invocation.Command.Count < 3 || string.IsNullOrWhiteSpace(invocation.Command[2]))
			throw new CliUserException("InvalidArguments", $"{command} 需要位置参数：{command} <文件路径> --output <输出路径>。");
		return invocation.Command[2];
	}

	/// <summary>
	/// 规格里写的 <c>sections</c> 相对**规格文件所在目录**解析：规格和映射表通常一起放在 mod 目录里，
	/// 用相对路径才能整目录搬走。命令行 <c>--sections</c> 优先级更高，相对当前目录。
	/// </summary>
	private static string ResolveSectionsPath(string? fromOptions, string? fromSpec, string specPath)
	{
		if (!string.IsNullOrWhiteSpace(fromOptions))
			return Path.GetFullPath(fromOptions);
		if (string.IsNullOrWhiteSpace(fromSpec))
			throw new CliUserException(
				"WardrobeSectionsRequired",
				"规格里没有写 sections，也没有通过 --sections 指定；编译需要构建期 section 映射表来查指纹。");
		var directory = Path.GetDirectoryName(specPath) ?? Directory.GetCurrentDirectory();
		return Path.GetFullPath(Path.Combine(directory, fromSpec));
	}

	private static string RequireOutput(CliGlobalOptions options, string command)
	{
		if (string.IsNullOrWhiteSpace(options.Output))
			throw new CliUserException("OutputRequired", $"{command} 必须显式提供 --output <文件路径>。");
		return options.Output;
	}

	/// <summary>
	/// 只允许命令自己声明的选项。放任多余开关是危险的：用户以为 <c>--confirm</c> 有作用，
	/// 而命令其实根本没看它，比直接报错更容易让人误判。
	/// </summary>
	private static void EnsureOptions(CliGlobalOptions options, string command, params string[] allowed)
	{
		var used = new List<string>();
		if (!string.IsNullOrWhiteSpace(options.ManagerRoot)) used.Add("--manager-root");
		if (!string.IsNullOrWhiteSpace(options.Library)) used.Add("--library");
		if (!string.IsNullOrWhiteSpace(options.GameData)) used.Add("--game-data");
		if (!string.IsNullOrWhiteSpace(options.RegistryRoot)) used.Add("--registry-root");
		if (!string.IsNullOrWhiteSpace(options.Output)) used.Add("--output");
		if (!string.IsNullOrWhiteSpace(options.Plan)) used.Add("--plan");
		if (!string.IsNullOrWhiteSpace(options.File)) used.Add("--file");
		if (!string.IsNullOrWhiteSpace(options.Sections)) used.Add("--sections");
		if (!string.IsNullOrWhiteSpace(options.Host)) used.Add("--host");
		if (!string.IsNullOrWhiteSpace(options.AddToProfile)) used.Add("--add-to-profile");
		if (!string.IsNullOrWhiteSpace(options.Keys)) used.Add("--keys");
		if (!string.IsNullOrWhiteSpace(options.AddKeys)) used.Add("--add-key");
		if (!string.IsNullOrWhiteSpace(options.RemoveKeys)) used.Add("--remove-key");
		if (!string.IsNullOrWhiteSpace(options.ArchiveId)) used.Add("--archive-id");
		if (!string.IsNullOrWhiteSpace(options.Kind)) used.Add("--kind");
		if (!string.IsNullOrWhiteSpace(options.Header)) used.Add("--header");
		if (!string.IsNullOrWhiteSpace(options.AvatarToc)) used.Add("--avatar-toc");
		if (!string.IsNullOrWhiteSpace(options.BoneHash)) used.Add("--bone-hash");
		if (!string.IsNullOrWhiteSpace(options.Components)) used.Add("--components");
		if (!string.IsNullOrWhiteSpace(options.Name)) used.Add("--name");
		if (!string.IsNullOrWhiteSpace(options.MaterialSource)) used.Add("--material-source");
		if (options.AlbedoFactor is not null) used.Add("--albedo-factor");
		if (!string.IsNullOrWhiteSpace(options.Matcap)) used.Add("--matcap");
		if (!string.IsNullOrWhiteSpace(options.Donor)) used.Add("--donor");
		if (options.Picks.Count > 0) used.Add("--pick");
		if (options.Position is not null) used.Add("--position");
		if (options.Limit is not null) used.Add("--limit");
		if (!string.IsNullOrWhiteSpace(options.Cursor)) used.Add("--cursor");
		if (options.SetActive) used.Add("--set-active");
		if (options.WithUnsharedOptions) used.Add("--with-unshared-options");
		if (options.WithEffective) used.Add("--with-effective");
		if (options.OnlyCompetition) used.Add("--only-competition");
		if (options.Overwrite) used.Add("--overwrite");
		if (options.Confirm) used.Add("--confirm");

		foreach (var name in used)
		{
			if (Array.Exists(allowed, candidate => string.Equals(candidate, name, StringComparison.Ordinal)))
				continue;
			throw new CliUserException(
				"CommandOptionNotSupported",
				$"{command} 仅接受 {string.Join("、", allowed)} 与 --json，收到 {name}。");
		}
	}

	private static void Emit(CliGlobalOptions options, object json, Func<string> human)
	{
		if (options.Json)
			CliOutput.WriteJson(json);
		else
			Console.WriteLine(human());
	}
}
