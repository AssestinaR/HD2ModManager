using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ModAnalyzeCliTests
{
	[Fact]
	public async Task ModAnalyze_UsesDirectFullPatchAnalysis_AndDoesNotWriteManagerState()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		const ulong unitId = 0x101;
		const ulong materialId = 0x202;
		const ulong textureId = 0x303;
		var patchPath = Path.Combine(fixture.LibraryRoot, fixture.StandardC.RelativePath, "3333333333333333.patch_0");
		var patch = CreatePatch(new Dictionary<AssetKey, byte[]>
		{
			[new AssetKey(PatchUnitMeshReader.UnitTypeId, unitId)] = CreateUnitPayload(7, materialId),
			[new AssetKey(MaterialDependencyResolver.MaterialTypeId, materialId)] = CreateMaterialPayload(textureId),
			[new AssetKey(MaterialDependencyResolver.TextureTypeId, textureId)] = [1, 2, 3],
			[new AssetKey(LuaAssetFacts.BootTypeId, LuaAssetFacts.BootFileId)] = [4, 5],
		});
		await File.WriteAllBytesAsync(patchPath, patch);
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var profilesBefore = await File.ReadAllBytesAsync(fixture.ProfilesPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.OptionActivationsPath);
		var patchBefore = await File.ReadAllBytesAsync(patchPath);
		var paths = new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot);
		Assert.False(File.Exists(paths.ModFactsDbPath));
		Assert.False(Directory.Exists(Path.Combine(paths.IndexDirectory, "mod-information")));

		var result = await CliTestConsole.RunCapturedAsync(
			"mod", "analyze", fixture.StandardCSelector,
			"--library", fixture.LibraryRoot,
			"--json");
		Assert.Equal(0, result.ExitCode);
		using var document = result.ParseJson();
		Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
		var analysis = document.RootElement.GetProperty("analysis");
		Assert.Equal("hd2mod.mod-analysis", analysis.GetProperty("schema").GetString());
		var group = Assert.Single(analysis.GetProperty("patchGroups").EnumerateArray());
		Assert.Equal("3333333333333333", group.GetProperty("archiveHex16").GetString());
		Assert.Equal(0, group.GetProperty("patchIndex").GetInt32());
		Assert.Equal("full", group.GetProperty("depth").GetString());
		Assert.Equal("asset-key", group.GetProperty("referenceScope").GetString());
		Assert.Equal(4, group.GetProperty("entries").GetArrayLength());
		Assert.Equal(2, group.GetProperty("references").GetArrayLength());

		var entries = group.GetProperty("entries").EnumerateArray().ToArray();
		var unit = Assert.Single(entries, entry => entry.GetProperty("assetKey").GetProperty("fileId").GetString() == "0x0000000000000101");
		Assert.Equal("unit", unit.GetProperty("typeClass").GetString());
		Assert.True(unit.GetProperty("entryIndex").GetUInt32() > 0);
		Assert.True(unit.TryGetProperty("unit", out var unitSummary));
		Assert.False(unitSummary.GetProperty("isReadable").GetBoolean());
		Assert.False(string.IsNullOrWhiteSpace(unitSummary.GetProperty("readError").GetString()));
		var lua = Assert.Single(entries, entry => entry.GetProperty("assetKey").GetProperty("fileId").GetString() == $"0x{LuaAssetFacts.BootFileId:x16}");
		Assert.Equal("lua", lua.GetProperty("typeClass").GetString());
		Assert.Equal("Boot", lua.GetProperty("luaKind").GetString());
		var references = group.GetProperty("references").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToArray();
		Assert.Contains("unitMaterial", references);
		Assert.Contains("materialTexture", references);

		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(profilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
		Assert.Equal(patchBefore, await File.ReadAllBytesAsync(patchPath));
		Assert.False(File.Exists(paths.ModFactsDbPath));
		Assert.False(Directory.Exists(Path.Combine(paths.IndexDirectory, "mod-information")));
	}

	[Fact]
	public async Task ModAnalyze_InvalidToc_ReturnsStructuredPartialDiagnosticsWithoutThrowing()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var result = await CliTestConsole.RunCapturedAsync(
			"mod", "analyze", fixture.StandardASelector,
			"--library", fixture.LibraryRoot,
			"--json");
		Assert.Equal(2, result.ExitCode);
		using var document = result.ParseJson();
		Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
		var group = Assert.Single(document.RootElement.GetProperty("analysis").GetProperty("patchGroups").EnumerateArray());
		var diagnostics = group.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
		Assert.Contains("InvalidToc", diagnostics);
		Assert.Empty(group.GetProperty("entries").EnumerateArray());
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
		const int typeRecordSize = 32;
		const int entryRecordSize = 80;
		var types = entries.Keys.Select(key => key.TypeId).Distinct().ToArray();
		var entryOffset = typeOffset + types.Length * typeRecordSize;
		var payloadOffset = entryOffset + entries.Count * entryRecordSize;
		var data = new byte[payloadOffset + entries.Sum(pair => pair.Value.Length)];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0xf0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), (uint)types.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)entries.Count);
		for (var index = 0; index < types.Length; index++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(typeOffset + index * typeRecordSize + 8, 8), types[index]);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(typeOffset + index * typeRecordSize + 16, 8), (ulong)entries.Count(pair => pair.Key.TypeId == types[index]));
		}

		var offset = payloadOffset;
		foreach (var (key, payload, index) in entries.Select((pair, index) => (pair.Key, pair.Value, index)))
		{
			var entry = entryOffset + index * entryRecordSize;
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry, 8), key.FileId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry + 8, 8), key.TypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entry + 16, 8), (ulong)offset);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 56, 4), (uint)payload.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 76, 4), (uint)(index + 1));
			payload.CopyTo(data.AsSpan(offset));
			offset += payload.Length;
		}
		return data;
	}
}
