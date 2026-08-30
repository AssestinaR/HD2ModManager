using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HD2ModCli;

public sealed record CliSkillExportedFile(string Name, string DirectoryName, string RelativePath, string Sha256, long Bytes);

public sealed record CliSkillExportResult(string OutputDirectory, string ManifestPath, IReadOnlyList<CliSkillExportedFile> Skills);

/// <summary>
/// Exports self-contained skill directories without ever deleting arbitrary
/// files from an Agent's skill parent directory. Existing managed targets need
/// explicit --overwrite, but unrelated sibling skills are left untouched.
/// </summary>
public static class CliSkillExporter
{
	private const string ManifestFileName = "hd2mod-agent-skills.manifest.json";
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	public static async Task<CliSkillExportResult> ExportAsync(
		string outputDirectory,
		IReadOnlyList<string>? selectedSkillNames,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(outputDirectory))
			throw new CliUserException("OutputRequired", "agent skills export 必须显式提供 --output <Skill 父目录>。");
		var root = CliOutput.NormalizeDirectoryPath(outputDirectory, "SkillOutputDirectoryInvalid");
		if (File.Exists(root))
			throw new CliUserException("SkillOutputDirectoryInvalid", $"Skill 输出路径不是目录：{root}");

		var definitions = ResolveDefinitions(selectedSkillNames);
		var payloads = definitions
			.Select(definition => new SkillPayload(definition, Utf8NoBom.GetBytes(CliSkillCatalog.GetContent(definition.Name))))
			.ToArray();
		var manifestPath = Path.Combine(root, ManifestFileName);
		EnsureSafeDestinations(root, payloads, manifestPath, overwrite);

		Directory.CreateDirectory(root);
		var exported = new List<CliSkillExportedFile>(payloads.Length);
		foreach (var payload in payloads)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var destinationDirectory = Path.Combine(root, payload.Definition.DirectoryName);
			Directory.CreateDirectory(destinationDirectory);
			var destination = Path.Combine(destinationDirectory, "SKILL.md");
			await WriteManagedFileAsync(destination, payload.Bytes, overwrite, cancellationToken).ConfigureAwait(false);
			exported.Add(CreateExportedFile(payload.Definition, payload.Bytes));
		}
		var manifest = new
		{
			schema = "hd2mod.agent-skills-manifest",
			version = 1,
			scope = "current-export",
			protocolVersion = CliProjections.ProtocolVersion,
			cliVersion = GetVersion(),
			exportedUtc = DateTimeOffset.UtcNow,
			skills = exported,
		};
		await WriteManagedFileAsync(manifestPath, Utf8NoBom.GetBytes(CliOutput.ToJson(manifest)), overwrite, cancellationToken).ConfigureAwait(false);
		return new CliSkillExportResult(root, manifestPath, exported);
	}

	private static IReadOnlyList<CliSkillDefinition> ResolveDefinitions(IReadOnlyList<string>? selectedSkillNames)
	{
		if (selectedSkillNames is null || selectedSkillNames.Count == 0)
			return CliSkillCatalog.ListCapabilitySkills();
		var definitions = selectedSkillNames
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(CliSkillCatalog.ResolveCapability)
			.ToArray();
		if (definitions.Length == 0)
			throw new CliUserException("AgentSkillRequired", "请提供要导出的能力 Skill 名称，或省略名称以导出全部能力 Skill。");
		return definitions;
	}

	private static void EnsureSafeDestinations(string root, IReadOnlyList<SkillPayload> payloads, string manifestPath, bool overwrite)
	{
		foreach (var payload in payloads)
		{
			var destinationDirectory = Path.Combine(root, payload.Definition.DirectoryName);
			var destination = Path.Combine(destinationDirectory, "SKILL.md");
			if (File.Exists(destinationDirectory))
				throw new CliUserException("SkillOutputDirectoryInvalid", $"Skill 目标目录被同名文件占用：{destinationDirectory}");
			if (Directory.Exists(destination))
				throw new CliUserException("SkillOutputDirectoryInvalid", $"Skill 输出文件路径被目录占用：{destination}");
			if (!overwrite && Directory.Exists(destinationDirectory))
				throw new CliUserException("SkillOutputAlreadyExists", $"Skill 目标目录已存在：{destinationDirectory}。不会猜测其归属；如需仅覆盖其中的 SKILL.md，请显式传入 --overwrite。");
		}

		if (Directory.Exists(manifestPath))
			throw new CliUserException("SkillOutputDirectoryInvalid", $"Skill 清单路径被目录占用：{manifestPath}");
		if (!overwrite && File.Exists(manifestPath))
			throw new CliUserException("SkillOutputAlreadyExists", $"Skill 清单已存在：{manifestPath}。如需刷新此输出目录的清单，请显式传入 --overwrite。");
	}

	private static CliSkillExportedFile CreateExportedFile(CliSkillDefinition definition, byte[] bytes)
		=> new(
			definition.Name,
			definition.DirectoryName,
			Path.Combine(definition.DirectoryName, "SKILL.md").Replace('\\', '/'),
			Convert.ToHexString(SHA256.HashData(bytes)),
			bytes.LongLength);

	private static async Task WriteManagedFileAsync(string path, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
	{
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, path, overwrite);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}

	private static string GetVersion()
		=> Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
			?? "dev";

	private sealed record SkillPayload(CliSkillDefinition Definition, byte[] Bytes);
}
