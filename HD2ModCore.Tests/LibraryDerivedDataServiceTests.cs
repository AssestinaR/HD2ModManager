using System.Globalization;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies unified derived library data for file-system-backed mod facts.
public sealed class LibraryDerivedDataServiceTests
{
	[Fact]
	public async Task BuildAsync_IndexesDirectoryIconPatchFilesAndAssetSummary()
	{
		var root = CreateTempRoot();
		try
		{
			var paths = new StoragePaths(root);
			var modsRoot = Path.Combine(root, "mods");
			var node = CreateNode("mod-a", "Mod A");
			var modDir = Path.Combine(modsRoot, node.RelativePath);
			Directory.CreateDirectory(modDir);

			WriteMetadata(paths, "aaaaaaaaaaaaaaaa", "Armor A", fileId: 100, fileName: "content/armor/a_body", typeId: 0xe0a48d0be9a7453f, typeName: "unit");
			await File.WriteAllBytesAsync(Path.Combine(modDir, "icon.png"), new byte[] { 1, 2, 3 });
			await File.WriteAllBytesAsync(Path.Combine(modDir, "aaaaaaaaaaaaaaaa.patch_0"), BuildToc(new[] { new AssetKey(0xe0a48d0be9a7453f, 100) }));

			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode> { [node.Id] = node },
				Array.Empty<Profile>());
			await using var informationReader = CoreServices.CreateModInformationReader();
			await using var informationCenter = CoreServices.CreateModInformationCenter(paths, informationReader);
			var service = CoreServices.CreateLibraryDerivedDataService(paths, informationCenter);

			var derived = await service.BuildAsync(snapshot, modsRoot);

			var nodeData = derived.Find(node.Id);
			Assert.NotNull(nodeData);
			Assert.True(nodeData.DirectoryExists);
			Assert.Equal(Path.Combine(modDir, "icon.png"), nodeData.IconPath);
			Assert.Single(nodeData.PatchFiles, f => f.SidecarKind == PatchSidecarKind.Base);
			Assert.Single(nodeData.ContentFacts.PatchGroups);
			Assert.Equal(node.Id, nodeData.ContentFacts.PatchGroups[0].Id.NodeId);
			Assert.False(string.IsNullOrWhiteSpace(nodeData.ContentFacts.ContentGeneration));
			Assert.NotNull(nodeData.AssetSummary);
			// No asset index proves Unit ownership here. A container name alone must
			// not label a new/private Unit as replacing the container's old armor.
			Assert.DoesNotContain("armor", nodeData.AssetSummary!.DerivedTags);
			Assert.Contains("model", nodeData.AssetSummary.DerivedTags);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	private static string CreateTempRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static ModNode CreateNode(string relativePath, string name)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null),
			Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

	private static void WriteMetadata(StoragePaths paths, string archiveId, string archiveName, ulong fileId, string fileName, ulong typeId, string typeName)
	{
		Directory.CreateDirectory(paths.ResourcesDirectory);
		var archives = new Dictionary<string, Dictionary<string, string>>
		{
			["Armor"] = new Dictionary<string, string>
			{
				[archiveId] = archiveName,
			},
		};
		File.WriteAllText(paths.ArchiveHashesPath, JsonSerializer.Serialize(archives));
		File.WriteAllText(paths.FriendlyNamesPath, $"{fileId.ToString(CultureInfo.InvariantCulture)} {fileName}{Environment.NewLine}");
		File.WriteAllText(paths.TypeHashesPath, $"{typeId:x16} {typeName}{Environment.NewLine}");
	}

	private static byte[] BuildToc(AssetKey[] entries)
	{
		const uint magic = 4026531857;
		var numTypes = 0;
		var numFiles = entries.Length;
		var entriesOffset = 60 + numTypes * 32;
		var totalSize = entriesOffset + numFiles * 80;
		var buffer = new byte[totalSize];

		WriteUInt32(buffer, 0, magic);
		WriteUInt32(buffer, 4, (uint)numTypes);
		WriteUInt32(buffer, 8, (uint)numFiles);

		var offset = entriesOffset;
		foreach (var e in entries)
		{
			WriteUInt64(buffer, offset, e.FileId);
			WriteUInt64(buffer, offset + 8, e.TypeId);
			offset += 80;
		}

		return buffer;
	}

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
	{
		buffer[offset + 0] = (byte)(value & 0xFF);
		buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
		buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
		buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
	}

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
	{
		buffer[offset + 0] = (byte)(value & 0xFF);
		buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
		buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
		buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
		buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
		buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
		buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
		buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
	}

	private static void DeleteQuietly(string path)
	{
		try { Directory.Delete(path, recursive: true); } catch { }
	}
}
