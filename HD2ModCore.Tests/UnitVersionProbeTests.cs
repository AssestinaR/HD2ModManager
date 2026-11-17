using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Tests;

// Purpose: Verifies the lightweight Unit version probe recognizes the confirmed legacy Unit layout without parsing mesh data.
public sealed class UnitVersionProbeTests
{
	[Fact]
	public async Task ProbeAsync_ReadsLegacyUnitVersion_AndReportsOutdated()
	{
		var path = Path.Combine(Path.GetTempPath(), "hd2-unit-version-" + Guid.NewGuid().ToString("N") + ".patch_0");
		try
		{
			var payload = new byte[0x30];
			BitConverter.GetBytes(0x00a4cd34U).CopyTo(payload, 0x2c);
			await File.WriteAllBytesAsync(path, payload);
			var key = new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 0x1234UL);
			var entry = new AdaptationPatchTocEntry(key, path, Path.GetFileName(path), 0, 0, 0, 0, 0, (uint)payload.Length, 0, 0, 0, 0, 0);
			var analysis = new PatchGroupAnalysis(new PatchGroupInput(path), [], [], [], DateTimeOffset.UtcNow, "test", PatchAnalysisDepth.Inventory, [entry]);

			var evidence = await new UnitVersionProbe().ProbeAsync(analysis);
			var report = ModUnitCompatibilityReport.FromEvidence(evidence);

			var item = Assert.Single(evidence);
			Assert.Equal(0x00a4cd34U, item.Version);
			Assert.Equal(UnitCompatibilityStatus.OutdatedConfirmed, report.Status);
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	[Fact]
	public void FromEvidence_RecognizesCurrentVersionOnlyAsCandidate()
	{
		var report = ModUnitCompatibilityReport.FromEvidence([
			new UnitVersionEvidence("sample.patch_0", new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 1), ModUnitCompatibilityReport.CurrentUnitVersion)
		]);

		Assert.Equal(UnitCompatibilityStatus.CurrentCandidate, report.Status);
		Assert.False(report.IsOutdated);
	}

	[Fact]
	public async Task Producer_UpdatesGenerationAndCompatibilityWhenSectionalOverwriteUpgrades()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-unit-version-sectional-" + Guid.NewGuid().ToString("N"));
		try
		{
			var modDir = Path.Combine(root, "mod-a");
			var sectionalDir = Path.Combine(modDir, "sectional");
			var overwriteDir = Path.Combine(modDir, "Overwrite");
			Directory.CreateDirectory(sectionalDir);
			Directory.CreateDirectory(overwriteDir);

			await WriteUnitPatchAsync(modDir, "9ba626afa44a3aa3.patch_0", 0x00a4cd35U);
			await WriteUnitPatchAsync(sectionalDir, "9ba626afa44a3aa3.patch_0", 0x00a4cd35U);
			await WriteUnitPatchAsync(overwriteDir, "9ba626afa44a3aa3.patch_0", 0x00a4cd35U);

			var node = new ModNode(ModNodeId.New(), "mod-a", new ModNodeMetadata("mod-a", null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());
			await using var reader = CoreServices.CreateModInformationReader();
			var producer = CoreServices.CreateUnitVersionInformationProducer(reader);

			var before = await producer.ProduceAsync(node, root);
			Assert.True(before.Report.IsOutdated);

			await Task.Delay(20);
			await WriteUnitPatchAsync(sectionalDir, "9ba626afa44a3aa3.patch_0", ModUnitCompatibilityReport.CurrentUnitVersion);
			await WriteUnitPatchAsync(overwriteDir, "9ba626afa44a3aa3.patch_0", ModUnitCompatibilityReport.CurrentUnitVersion);
			reader.InvalidateNode(node.Id);

			var after = await producer.ProduceAsync(node, root);
			Assert.False(after.Report.IsOutdated);
			Assert.Equal(UnitCompatibilityStatus.CurrentCandidate, after.Report.Status);
			Assert.NotEqual(before.Generation, after.Generation);
		}
		finally
		{
			try { Directory.Delete(root, true); } catch { }
		}
	}

	private static async Task WriteUnitPatchAsync(string directory, string name, uint unitVersion)
	{
		var toc = new byte[0x30];
		BitConverter.GetBytes(unitVersion).CopyTo(toc, 0x2c);
		var session = new CanonicalPatchSession();
		session.AddEntry(new CanonicalPatchSessionEntry(
			new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 0x1234UL),
			CanonicalPatchEntryOwnership.TargetOutput,
			toc,
			[],
			[]));
		Assert.True(session.Finalize(CanonicalDependencyClosureValidation.Valid).IsValid);
		await new CanonicalPatchWriter().WriteAsync(session, directory, name, null, overwriteExisting: true);
	}
}
