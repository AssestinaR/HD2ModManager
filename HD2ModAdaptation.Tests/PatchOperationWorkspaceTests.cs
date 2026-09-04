using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using System.Text.Json;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class PatchOperationWorkspaceTests
{
	[Fact]
	public void StageMovesAllPayloadOwnershipToOperationFilesAndDisposesThem()
	{
		var output = Path.Combine(Path.GetTempPath(), "HD2ModAdaptationTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(output);
		string workspacePath;
		try
		{
			using (var workspace = new PatchOperationWorkspace(output, "unit-rebuild"))
			{
				workspacePath = workspace.DirectoryPath;
				var staged = workspace.Stage(new CanonicalPatchSessionEntry(
					new AssetKey(0x10, 0x20),
					CanonicalPatchEntryOwnership.TargetOutput,
					[1, 2], [3, 4], [5, 6]));

				Assert.Null(staged.TocData);
				Assert.Null(staged.GpuData);
				Assert.Null(staged.StreamData);
				Assert.Equal([1, 2], File.ReadAllBytes(staged.TocDataPath!));
				Assert.Equal([3, 4], File.ReadAllBytes(staged.GpuDataPath!));
				Assert.Equal([5, 6], File.ReadAllBytes(staged.StreamDataPath!));
				using var manifest = JsonDocument.Parse(File.ReadAllText(workspace.ManifestPath));
				Assert.Equal("unit-rebuild", manifest.RootElement.GetProperty("OperationKind").GetString());
				Assert.Equal(0x20UL, manifest.RootElement.GetProperty("Outputs")[0].GetProperty("Key").GetProperty("FileId").GetUInt64());
			}

			Assert.False(Directory.Exists(workspacePath));
		}
		finally
		{
			if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
		}
	}

	[Fact]
	public void StageBatchesManifestWritesAfterTheInitialRecoverableEntry()
	{
		var output = Path.Combine(Path.GetTempPath(), "HD2ModAdaptationTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(output);
		try
		{
			using var workspace = new PatchOperationWorkspace(output, "unit-rebuild");
			for (ulong index = 0; index < 2; index++)
				workspace.Stage(new CanonicalPatchSessionEntry(
					new AssetKey(0x10, index), CanonicalPatchEntryOwnership.TargetOutput, [1], [2], [3]));

			using var manifest = JsonDocument.Parse(File.ReadAllText(workspace.ManifestPath));
			Assert.Single(manifest.RootElement.GetProperty("Outputs").EnumerateArray());
		}
		finally
		{
			if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
		}
	}

	[Fact]
	public void StageKeepsLargeImmutableSourceRangesOutOfTheWorkspace()
	{
		var output = Path.Combine(Path.GetTempPath(), "HD2ModAdaptationTests", Guid.NewGuid().ToString("N"));
		var source = Path.Combine(output, "source.stream");
		Directory.CreateDirectory(output);
		try
		{
			File.WriteAllBytes(source, [9, 8, 7, 6]);
			using var workspace = new PatchOperationWorkspace(output, "unit-rebuild");
			var staged = workspace.Stage(new CanonicalPatchSessionEntry(
				new AssetKey(0x10, 0x21),
				CanonicalPatchEntryOwnership.TargetOutput,
				[1, 2], [3, 4], null)
			{
				StreamDataSource = new CanonicalPayloadSourceRange(source, 1, 2)
			});

			Assert.Null(staged.StreamData);
			Assert.Null(staged.StreamDataPath);
			Assert.Equal(new CanonicalPayloadSourceRange(source, 1, 2), staged.StreamDataSource);
			Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "0000000000000010-0000000000000021.stream")));
			using var manifest = JsonDocument.Parse(File.ReadAllText(workspace.ManifestPath));
			Assert.StartsWith("source:source.stream@1+2", manifest.RootElement.GetProperty("Outputs")[0].GetProperty("StreamFile").GetString());
		}
		finally
		{
			if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
		}
	}
}
