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
		var sections = await WardrobeSectionMapFile.ReadAsync(sectionsPath, cancellationToken).ConfigureAwait(false);
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
		var sections = await WardrobeSectionMapFile.ReadAsync(sectionsPath, cancellationToken).ConfigureAwait(false);
		var validation = WardrobeMaskCompiler.Validate(masks, sections);
		Emit(
			options,
			new
			{
				ok = validation.IsValid,
				validation = new
				{
					maskPath,
					sectionsPath,
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
				builder.Append($"  映射表：{sectionsPath}\n");
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
		if (options.AllowUnsafeLua) used.Add("--allow-unsafe-lua");

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
