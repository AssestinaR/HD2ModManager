using System.Buffers.Binary;
using PatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class RuntimeArmorResourceFilterTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DeployedUnits_FollowsContentForCopiedAndLinkedFiles(bool linked)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-unit-filter-" + Guid.NewGuid().ToString("N"));
		var source = Path.Combine(root, "library");
		var deployed = Path.Combine(root, "data");
		Directory.CreateDirectory(source);
		Directory.CreateDirectory(deployed);
		var name = "0000000000000001.patch_0";
		var patch = Path.Combine(source, name);
		var output = Path.Combine(deployed, name);
		try
		{
			var bytes = new byte[180];
			BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xf0000011);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(68), PatchUnitMeshReader.UnitTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(76), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(92), 0x044e0dee2a7b0001);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(100), PatchUnitMeshReader.UnitTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(108), 176);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(148), 4);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(152), 8);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(156), 16);
			await File.WriteAllBytesAsync(patch, bytes);
			await File.WriteAllBytesAsync(patch + ".stream", new byte[8]);
			await File.WriteAllBytesAsync(patch + ".gpu_resources", new byte[16]);
			foreach (var suffix in new[] { "", ".stream", ".gpu_resources" })
			{
				if (linked) File.CreateSymbolicLink(output + suffix, Path.GetRelativePath(deployed, patch + suffix));
				else File.Copy(patch + suffix, output + suffix);
			}

			// All entries share a stream size and zero offset. A linked non-empty
			// stream must not trigger the compatibility override.
			var entries = await new PatchTocScanner().ScanEntriesAsync(output);
			Assert.Equal(8u, Assert.Single(entries).StreamSize);
			Assert.False(entries[0].UsesEmptyStreamCompatibilityOverride);
			Assert.Contains(0x044e0dee2a7b0001UL, await RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(deployed));

			var gpuTarget = linked ? patch + ".gpu_resources" : output + ".gpu_resources";
			await File.WriteAllBytesAsync(gpuTarget, new byte[2]);
			Assert.Empty(await RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(deployed));
			await File.WriteAllBytesAsync(gpuTarget, new byte[16]);
			File.Delete(gpuTarget);
			Assert.Empty(await RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(deployed));
		}
		finally
		{
			// Unlink explicitly before cleaning the isolated test tree.
			foreach (var suffix in new[] { "", ".stream", ".gpu_resources" })
				File.Delete(output + suffix);
			Directory.Delete(root, recursive: true);
		}
	}
}
