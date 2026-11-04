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
//
// **2026-11-04：compile / validate / project 进入弃用期，下个发布周期移除。**
// 理由：它们产出的 wardrobe.masks 只有一段运行时读它，而那段 Lua（armor_registry_runtime）
// 第一行就是 require('ffi')，本版引擎没有 ffi ⇒ 加载即退出；管理器也从来没有写过这份文件。
// 也就是说这条链今天已经不可能生效。`wardrobe sections` 保留 —— 它给「补齐旧 Mod 的
// section 表」用，与掩码无关。

internal static class CliWardrobe
{
	internal const string SectionsCommand = "wardrobe sections";

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
