using System.Security.Cryptography;
using System.Text;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：按需生产 Unit 版本信息，不改变普通 AssetInventory 生产路径。
// Purpose: Produces Unit-version information on demand without changing AssetInventory production.
public sealed class UnitVersionInformationProducer : IUnitVersionInformationProducer
{
	private readonly IPatchGroupAnalysisProvider _analysisProvider;
	private readonly IUnitVersionProbe _probe;

	public UnitVersionInformationProducer(IPatchGroupAnalysisProvider analysisProvider, IUnitVersionProbe? probe = null)
	{
		_analysisProvider = analysisProvider ?? throw new ArgumentNullException(nameof(analysisProvider));
		_probe = probe ?? new UnitVersionProbe();
	}

	// 作用：版本探测只需要统一读取器提供的 Patch 目录；保留旧 provider 重载给测试和兼容调用。
	// Purpose: Unit-version probing only needs the reader-backed Patch catalog; preserve the provider overload for compatibility.
	public UnitVersionInformationProducer(IModInformationReader informationReader, IUnitVersionProbe? probe = null)
		: this(new ModInformationPatchGroupAnalysisProvider(
			informationReader,
			depth: PatchAnalysisDepth.Inventory), probe)
	{
	}

	public async ValueTask<ModUnitVersionFacts> ProduceAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		var analyses = await _analysisProvider.AnalyzeNodeAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		var evidence = new List<UnitVersionEvidence>();
		var issues = new List<CoreIssue>();
		foreach (var analysis in analyses)
		{
			var values = await _probe.ProbeAsync(analysis, cancellationToken).ConfigureAwait(false);
			evidence.AddRange(values);
			issues.AddRange(analysis.Issues.Select(issue => new CoreIssue(CoreIssueSeverity.Warning, issue.Code, issue.Message, issue.SourceFilePath, node.Id)));
		}
		return new ModUnitVersionFacts(node.Id, node.RelativePath, ComputeGeneration(node, modsRootDirectory), DateTimeOffset.UtcNow, ModUnitCompatibilityReport.FromEvidence(evidence), issues);
	}

	private static string ComputeGeneration(ModNode node, string root)
	{
		var directory = Path.Combine(root, node.RelativePath);
		var builder = new StringBuilder(node.Id.Value.ToString("N"));
		if (Directory.Exists(directory))
			foreach (var path in ModNodePayloadLayout.EnumerateEffectiveFilesByName(directory).Values.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
			{
				var info = new FileInfo(path);
				var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
				builder.Append('|').Append(relative.ToLowerInvariant()).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
			}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
	}
}
