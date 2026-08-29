using System.Buffers.Binary;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies the first read-only patch-group analysis contract and its TOC projection.
public sealed class PatchGroupAnalyzerTests
{
	[Fact]
	public async Task AnalyzeAsync_ProjectsAssetKindsFromCanonicalToc()
	{
		var directory = CreateTemporaryDirectory();
		try
		{
			var tocPath = Path.Combine(directory, "sample.patch_0");
			await File.WriteAllBytesAsync(tocPath, CreateToc(
				new AssetKey(PatchUnitMeshReader.UnitTypeId, 1),
				new AssetKey(PatchUnitMeshReader.CompositeUnitTypeId, 2),
				new AssetKey(MaterialDependencyResolver.MaterialTypeId, 3),
				new AssetKey(MaterialDependencyResolver.TextureTypeId, 4)));

			var result = await new PatchGroupAnalyzer().AnalyzeAsync(new PatchGroupInput(tocPath));

			Assert.True(result.IsSuccessful);
			Assert.Empty(result.Issues);
			Assert.Equal(4, result.Assets.Count);
			Assert.Single(result.Assets, asset => asset.IsUnit);
			Assert.Single(result.Assets, asset => asset.IsCompositeUnit);
			Assert.Single(result.Assets, asset => asset.IsMaterial);
			Assert.Single(result.Assets, asset => asset.IsTexture);
			Assert.Empty(result.References);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task AnalyzeAsync_ReadsUnitMaterialAndMaterialTextureReferences()
	{
		var directory = CreateTemporaryDirectory();
		try
		{
			const ulong unitId = 0x101;
			const ulong materialId = 0x202;
			const ulong textureId = 0x303;
			var tocPath = Path.Combine(directory, "references.patch_0");
			await File.WriteAllBytesAsync(tocPath, CreatePatch(new Dictionary<AssetKey, byte[]>
			{
				[new AssetKey(PatchUnitMeshReader.UnitTypeId, unitId)] = CreateUnitPayload(7, materialId),
				[new AssetKey(MaterialDependencyResolver.MaterialTypeId, materialId)] = CreateMaterialPayload(textureId),
				[new AssetKey(MaterialDependencyResolver.TextureTypeId, textureId)] = [1, 2, 3],
			}));

			var result = await new PatchGroupAnalyzer().AnalyzeAsync(new PatchGroupInput(tocPath));

			Assert.Empty(result.Issues);
			Assert.Collection(result.References.OrderBy(reference => reference.Kind),
				reference =>
				{
					Assert.Equal(PatchReferenceKind.UnitMaterial, reference.Kind);
					Assert.Equal(new AssetKey(PatchUnitMeshReader.UnitTypeId, unitId), reference.SourceAssetKey);
					Assert.Equal(new AssetKey(MaterialDependencyResolver.MaterialTypeId, materialId), reference.TargetAssetKey);
					Assert.Equal((uint)7, reference.SlotId);
					Assert.Equal((uint)0x7c, reference.PayloadRelativeOffset);
				},
				reference =>
				{
					Assert.Equal(PatchReferenceKind.MaterialTexture, reference.Kind);
					Assert.Equal(new AssetKey(MaterialDependencyResolver.MaterialTypeId, materialId), reference.SourceAssetKey);
					Assert.Equal(new AssetKey(MaterialDependencyResolver.TextureTypeId, textureId), reference.TargetAssetKey);
					Assert.Equal(0, reference.ReferenceIndex);
					Assert.Equal((uint)140, reference.PayloadRelativeOffset);
				});
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task AnalyzeInventoryAsync_ListsAssetsWithoutReadingAdvancedReferences()
	{
		var directory = CreateTemporaryDirectory();
		try
		{
			var tocPath = Path.Combine(directory, "inventory.patch_0");
			await File.WriteAllBytesAsync(tocPath, CreatePatch(new Dictionary<AssetKey, byte[]>
			{
				[new AssetKey(PatchUnitMeshReader.UnitTypeId, 0x101)] = CreateUnitPayload(7, 0x202),
				[new AssetKey(MaterialDependencyResolver.MaterialTypeId, 0x202)] = CreateMaterialPayload(0x303),
			}));

			var result = await new PatchGroupAnalyzer().AnalyzeInventoryAsync(new PatchGroupInput(tocPath));

			Assert.True(result.IsSuccessful);
			Assert.Equal(PatchAnalysisDepth.Inventory, result.Depth);
			Assert.Equal(2, result.Assets.Count);
			Assert.Empty(result.References);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task AnalyzeAsync_RecognizesOnlyTheExactLuaBootAssetKey()
	{
		var directory = CreateTemporaryDirectory();
		try
		{
			var tocPath = Path.Combine(directory, "lua.patch_0");
			await File.WriteAllBytesAsync(tocPath, CreateToc(
				new AssetKey(LuaAssetFacts.BootTypeId, LuaAssetFacts.BootFileId),
				new AssetKey(LuaAssetFacts.BootTypeId, 0x1111),
				new AssetKey(0x1111222233334444, LuaAssetFacts.BootFileId)));

			var result = await new PatchGroupAnalyzer().AnalyzeInventoryAsync(new PatchGroupInput(tocPath));

			Assert.Equal(3, result.Assets.Count);
			var lua = Assert.Single(result.Assets, asset => asset.IsLua);
			Assert.Equal(LuaAssetFacts.BootTypeId, lua.AssetKey.TypeId);
			Assert.Equal(LuaAssetFacts.BootFileId, lua.AssetKey.FileId);
			Assert.Equal("Boot", lua.Lua!.Kind);
			Assert.Null(result.Assets.Single(asset => asset.AssetKey.FileId == 0x1111).Lua);
			Assert.Null(result.Assets.Single(asset => asset.AssetKey.TypeId == 0x1111222233334444).Lua);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task AnalyzeAsync_MissingTocReturnsStructuredIssue()
	{
		var result = await new PatchGroupAnalyzer().AnalyzeAsync(new PatchGroupInput(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".patch_0")));

		Assert.False(result.IsSuccessful);
		var issue = Assert.Single(result.Issues);
		Assert.Equal("MissingToc", issue.Code);
		Assert.Empty(result.Assets);
	}

	[Fact]
	public async Task AnalyzeAsync_ReportsExternalUnitMaterialWithoutReferenceError()
	{
		var directory = CreateTemporaryDirectory();
		try
		{
			var tocPath = Path.Combine(directory, "external-material.patch_0");
			await File.WriteAllBytesAsync(tocPath, CreatePatch(new Dictionary<AssetKey, byte[]>
			{
				[new AssetKey(PatchUnitMeshReader.UnitTypeId, 1)] = CreateUnitPayload(7, 0x202),
			}));

			var result = await new PatchGroupAnalyzer().AnalyzeAsync(new PatchGroupInput(tocPath));

			Assert.Empty(result.Issues);
			var reference = Assert.Single(result.References);
			Assert.Equal(new AssetKey(MaterialDependencyResolver.MaterialTypeId, 0x202), reference.TargetAssetKey);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static string CreateTemporaryDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "HD2ModAdaptationTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static byte[] CreateToc(params AssetKey[] keys)
	{
		const int entryOffset = 60;
		var data = new byte[entryOffset + keys.Length * 80];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0xf0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)keys.Length);
		for (var index = 0; index < keys.Length; index++)
		{
			var offset = entryOffset + index * 80;
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), keys[index].FileId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 8, 8), keys[index].TypeId);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 56, 4), 1);
		}
		return data;
	}

	private static byte[] CreateUnitPayload(uint slotId, ulong materialId)
	{
		var data = new byte[0x84];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x70, 4), 0x74);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x74, 4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x78, 4), slotId);
		BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x7c, 8), materialId);
		return data;
	}

	private static byte[] CreateMaterialPayload(ulong textureId)
	{
		var data = new byte[148];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64, 4), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(140, 8), textureId);
		return data;
	}

	private static byte[] CreatePatch(IReadOnlyDictionary<AssetKey, byte[]> entries)
	{
		const int typeOffset = 60;
		var types = entries.Keys.Select(key => key.TypeId).Distinct().ToArray();
		var entryOffset = typeOffset + types.Length * 32;
		var payloadOffset = entryOffset + entries.Count * 80;
		var data = new byte[payloadOffset + entries.Sum(pair => pair.Value.Length)];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0xf0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)types.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)entries.Count);
		for (var index = 0; index < types.Length; index++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(typeOffset + index * 32 + 8, 8), types[index]);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(typeOffset + index * 32 + 16, 8), (ulong)entries.Count(pair => pair.Key.TypeId == types[index]));
		}

		var offset = payloadOffset;
		foreach (var (key, payload, index) in entries.Select((pair, index) => (pair.Key, pair.Value, index)))
		{
			var entry = entryOffset + index * 80;
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry, 8), key.FileId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry + 8, 8), key.TypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry + 16, 8), (ulong)offset);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 56, 4), (uint)payload.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 76, 4), (uint)(index + 1));
			payload.CopyTo(data, offset);
			offset += payload.Length;
		}

		return data;
	}
}
