using System.Text.RegularExpressions;

namespace HD2ModCore.Tests;

// 作用：约束 HD2ModManager 不绕过共享信息中心创建稳定事实基础设施。
// Purpose: Prevent HD2ModManager from bypassing the shared information center.
public sealed class ModInformationCenterArchitectureTests
{
	[Fact]
	public void ManagerProductionCodeDoesNotCreateInformationInfrastructureOrUseObsoleteCenterlessFactories()
	{
		var managerRoot = FindRepositoryRoot();
		var sourceFiles = Directory.EnumerateFiles(Path.Combine(managerRoot, "HD2ModManager"), "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			.Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
		var forbidden = new Regex(
			@"(?:CoreServices\.)?CreateModDataIndex\s*\(|new\s+(?:ModDataIndex|JsonModInformationCache|SqliteModFactsStore)\s*\(|new\s+(?:ModFileFactsProducer|ModContentFactsService|ReferenceGraphProducer|MaintenanceAnalysisProducer|UnitVersionInformationProducer|AdvancedUnitAnalysisProducer|ModThumbnailProducer)\s*\(",
			RegexOptions.Compiled);
		var violations = sourceFiles
			.SelectMany(path => File.ReadLines(path).Select((line, number) => (path, line, number: number + 1)))
			.Where(item => forbidden.IsMatch(item.line))
			.Select(item => $"{Path.GetRelativePath(managerRoot, item.path)}:{item.number}: {item.line.Trim()}")
			.ToArray();

		Assert.Empty(violations);
	}

	[Fact]
	public void CanonicalToolOperationsUseTheSharedPatchOperationWorkspace()
	{
		var root = FindRepositoryRoot();
		var sameKey = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));
		var crossArmor = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalCrossArmorOrchestrator.cs"));
		var helmetMerge = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "HelmetToArmorMergeService.cs"));

		Assert.Contains("IPatchOperationWorkspaceFactory", sameKey);
		Assert.Contains("operationWorkspace.Stage(rebuilt.Job)", sameKey);
		Assert.Contains("IPatchOperationWorkspaceFactory", crossArmor);
		Assert.Contains("operationWorkspace.Stage(outputTargetEntry)", crossArmor);
		Assert.DoesNotContain("StagePayloads(", crossArmor);
		Assert.Contains("IPatchOperationWorkspaceFactory", helmetMerge);
		Assert.Contains("StageEdit(operationWorkspace", helmetMerge);
		Assert.Contains("CacheScope = ModInformationCacheScope.None", helmetMerge);
	}

	[Fact]
	public void SharedPatchWriterPreservesUntouchedPayloadsAsSourceRanges()
	{
		var root = FindRepositoryRoot();
		var writer = File.ReadAllText(Path.Combine(root, "HD2ModAdaptation", "PatchReconstruction", "PatchWorkspace", "PatchWorkspaceWriter.cs"));

		Assert.Contains("CanonicalPayloadSourceRange", writer);
		Assert.DoesNotContain("private static byte[] ReadPayload", writer);
	}

	[Fact]
	public void PatchValidatorStreamsSourceGeometryComparisonPerUnit()
	{
		var root = FindRepositoryRoot();
		var validator = File.ReadAllText(Path.Combine(root, "HD2ModAdaptation", "PatchReconstruction", "Validation", "PatchValidator.cs"));

		Assert.Contains("ValidateSourceGeometryForUnitAsync", validator);
		Assert.DoesNotContain("Dictionary<AssetKey, PatchUnitMesh>", validator);
	}

	[Fact]
	public void SameKeyPlanningUsesCachedFactsAndDoesNotDecodeUnitPayloads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));
		var start = source.IndexOf("private async ValueTask<SameKeyReconstructionPlan> PlanAsync", StringComparison.Ordinal);
		var end = source.IndexOf("private static IReadOnlyList<TargetShellMeshMapping> BuildMappings", StringComparison.Ordinal);

		Assert.True(start >= 0 && end > start, "Unable to isolate Same-key planning implementation.");
		var planning = source[start..end];
		Assert.Contains("index?.Entries", planning);
		Assert.DoesNotContain("sourceReader.ReadAsync", planning);
		Assert.DoesNotContain("targetReader.ReadAsync", planning);
	}

	[Fact]
	public void SameKeyReconstructionUsesUnifiedReaderForSourcePatchAndUnitReads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalSameKeyReconstructionService.cs"));

		Assert.Contains("IModInformationReader", source);
		Assert.Contains("ReadSourcePatchIndexAsync", source);
		Assert.Contains("ReadCanonicalSourceUnitAsync", source);
		Assert.DoesNotContain("workspaceReader.ReadIndexAsync", source);
		Assert.DoesNotContain("new PatchUnitMeshReader()", source);
	}

	[Fact]
	public void CrossArmorReconstructionUsesUnifiedReaderForSourceSideReads()
	{
		var root = FindRepositoryRoot();
		var source = File.ReadAllText(Path.Combine(root, "HD2ModCore", "Infrastructure", "CanonicalCrossArmorOrchestrator.cs"));

		Assert.Contains("IModInformationReader", source);
		Assert.Contains("ReadSourcePatchIndexAsync", source);
		Assert.Contains("ReadSourcePayloadAsync", source);
		Assert.Contains("ReadSourceUnitAsync", source);
		Assert.DoesNotContain("patchWorkspaceReader.ReadIndexAsync", source);
		Assert.DoesNotContain("sourcePayloadReader.ReadPayloadAsync", source);
		Assert.DoesNotContain("sourceReader.ReadAsync", source);
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}
}
