using System.Buffers.Binary;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationLuaAssetKeys = HD2ModAdaptation.PatchReconstruction.Lua.LuaAssetKeys;
using AdaptationLuaResourceFormat = HD2ModAdaptation.PatchReconstruction.Lua.LuaResourceFormat;

namespace HD2ModCore.Tests;

// 作用：验证"加载器类 mod"（一个 patch 里既有引导脚本、又有它自己的模块和 gpu 资源）
// 能被原样安装，并且管理器**不再**尝试解析或合并第三方 Lua。
//
// 为什么必须有这些用例：2026-10-28 的真实故障是 Chef Shared Loader 在真映射模式下
// 部署失败。修完之后的约定是：第三方 Lua patch 一律原样部署，管理器只负责自己那份
// 运行时；Lua 启动入口被多方争用时只报告，不擅自合并。
public sealed class LuaDeploymentLoaderPackageTests
{
	private const string BootSource =
		"local BC = \"\\27\\76\\117\\97\\81\\0\"\n"
		+ "local chunk = loadstring(BC)\n"
		+ "local boot_ok, boot_ret = pcall(chunk)\n"
		+ "return boot_ret\n";

	private const string SiblingSource =
		"-- SIBLING-RESOURCE-CONTENT the loader's own core module\n"
		+ "return function() end\n";

	public LuaDeploymentLoaderPackageTests() => LuaDeploymentService.MappingMode = ArmorMappingMode.TrueMapping;

	[Fact]
	public async Task Plan_SingleLoaderPackage_IsDeployedUnchangedWithoutErrors()
	{
		var directory = CreateScratch();
		try
		{
			var patchPath = Path.Combine(directory, "9ba626afa44a3aa3.patch_0");
			await File.WriteAllBytesAsync(patchPath, BuildLuaPatch(
				(AdaptationLuaAssetKeys.BootFileId, Encoding.UTF8.GetBytes(BootSource)),
				(0xC27789387E64579AUL, Encoding.UTF8.GetBytes(SiblingSource))));
			await File.WriteAllBytesAsync(patchPath + ".gpu_resources", new byte[64]);
			await File.WriteAllBytesAsync(patchPath + ".stream", []);

			var nodeId = ModNodeId.New();
			var profile = CreateProfile(nodeId);
			var snapshot = CreateSnapshot(profile, nodeId);
			var index = CreateIndex(nodeId, patchPath, gpuLength: 64);

			var result = await new LuaDeploymentService().PlanAsync(
				profile,
				snapshot,
				index,
				Path.Combine(directory, "mods"),
				Path.Combine(directory, "game", "data"),
				new Dictionary<ModNodeId, ModContentFacts>());

			Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
			// 只有一份提供者，没有需要管理器操心的事：不生成任何补丁，patch 索引原样。
			Assert.Empty(result.GeneratedPatchGroups);
			Assert.False(result.RequiresMaterialization);
			var deployed = result.PatchIndex.FilesByNode[nodeId];
			Assert.Equal(3, deployed.Count);
			Assert.Contains(deployed, file => file.SidecarKind == PatchSidecarKind.Base && file.FilePath == patchPath);
			Assert.Contains(deployed, file => file.SidecarKind == PatchSidecarKind.GpuResources && file.Length == 64);
		}
		finally
		{
			TryDelete(directory);
		}
	}

	[Fact]
	public async Task Plan_TwoLoaderPackages_OnlyWarnsAboutTheEntryOverlapAndStillDeploysBoth()
	{
		var directory = CreateScratch();
		try
		{
			var firstPath = Path.Combine(directory, "first", "9ba626afa44a3aa3.patch_0");
			Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
			await File.WriteAllBytesAsync(firstPath, BuildLuaPatch(
				(AdaptationLuaAssetKeys.BootFileId, Encoding.UTF8.GetBytes(BootSource)),
				(0xC27789387E64579AUL, Encoding.UTF8.GetBytes(SiblingSource))));

			var secondPath = Path.Combine(directory, "second", "9ba626afa44a3aa3.patch_0");
			Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
			await File.WriteAllBytesAsync(secondPath, BuildLuaPatch(
				(AdaptationLuaAssetKeys.BootFileId, Encoding.UTF8.GetBytes(BootSource)),
				(0xC27789387E64579AUL, Encoding.UTF8.GetBytes(SiblingSource))));

			var firstNode = ModNodeId.New();
			var secondNode = ModNodeId.New();
			var profile = CreateProfile(firstNode, secondNode);
			var snapshot = CreateSnapshot(profile, firstNode, secondNode);
			var index = CreateIndex(firstNode, firstPath, secondNode, secondPath);

			var result = await new LuaDeploymentService().PlanAsync(
				profile,
				snapshot,
				index,
				Path.Combine(directory, "mods"),
				Path.Combine(directory, "game", "data"),
				new Dictionary<ModNodeId, ModContentFacts>());

			// 两个 mod 抢同一个 Lua 启动入口，但这不是管理器的功能失败：报告一句就走，
			// 两个 patch 都原样部署（引擎只会执行序号最大的那份）。
			Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
			var warning = Assert.Single(result.Issues, issue => issue.Code == "LuaBootEntryOverlap");
			Assert.Equal(CoreIssueSeverity.Warning, warning.Severity);
			Assert.Empty(result.GeneratedPatchGroups);
			Assert.Contains(result.PatchIndex.FilesByNode[firstNode], file => file.FilePath == firstPath);
			Assert.Contains(result.PatchIndex.FilesByNode[secondNode], file => file.FilePath == secondPath);
		}
		finally
		{
			TryDelete(directory);
		}
	}

	// 造一个"多资源 Lua patch"：类型表在 0x48，条目表紧随其后，载荷按条目 offset 落位。
	// 这就是 Chef Shared Loader 那种包的形状（引导桩 + 自身模块 + 可能还有材质/gpu）。
	private static byte[] BuildLuaPatch(params (ulong FileId, byte[] Payload)[] resources)
	{
		const int headerLength = 0x48;
		const int typeRecordSize = 32;
		const int entryRecordSize = 80;
		var entriesOffset = headerLength + typeRecordSize;
		var dataOffset = entriesOffset + entryRecordSize * resources.Length;
		var total = dataOffset + resources.Sum(resource => 8 + resource.Payload.Length);
		var bytes = new byte[total];

		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)resources.Length);
		// 类型表行：+8 是类型 id，+16 是声明条目数（扫描器靠它挑出正确布局）。
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerLength + 8), AdaptationLuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(headerLength + 16), (ulong)resources.Length);

		var cursor = dataOffset;
		for (var index = 0; index < resources.Length; index++)
		{
			var row = entriesOffset + entryRecordSize * index;
			var envelopeLength = 8 + resources[index].Payload.Length;
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(row), resources[index].FileId);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(row + 8), AdaptationLuaAssetKeys.LuaTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(row + 16), (ulong)cursor);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row + 56), (uint)envelopeLength);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(row + 76), (uint)(index + 1));
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor), (uint)resources[index].Payload.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 4), AdaptationLuaResourceFormat.LegacySourceVersion);
			resources[index].Payload.CopyTo(bytes.AsSpan(cursor + 8));
			cursor += envelopeLength;
		}
		return bytes;
	}

	private static Profile CreateProfile(params ModNodeId[] nodes)
		=> new(
			ProfileId.New(),
			"test",
			DateTimeOffset.UtcNow,
			null,
			nodes.Select((node, index) => new ProfileEntry(node, index)).ToArray());

	private static LibrarySnapshot CreateSnapshot(Profile profile, params ModNodeId[] nodes)
		=> new(
			1,
			DateTimeOffset.UtcNow,
			nodes.ToDictionary(
				node => node,
				node => new ModNode(node, node.Value.ToString("N"), new ModNodeMetadata("test", null, DateTimeOffset.UtcNow, null), [], [])),
			[profile],
			profile.Id);

	private static PatchFileIndex CreateIndex(ModNodeId nodeId, string patchPath, long gpuLength = 0)
		=> new(
			DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>
			{
				[nodeId] =
				[
					new IndexedPatchFile(nodeId, patchPath, Path.GetFileName(patchPath), "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Base, new FileInfo(patchPath).Length, DateTimeOffset.UtcNow),
					new IndexedPatchFile(nodeId, patchPath + ".stream", Path.GetFileName(patchPath) + ".stream", "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Stream, 0, DateTimeOffset.UtcNow),
					new IndexedPatchFile(nodeId, patchPath + ".gpu_resources", Path.GetFileName(patchPath) + ".gpu_resources", "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.GpuResources, gpuLength, DateTimeOffset.UtcNow),
				],
			},
			[]);

	private static PatchFileIndex CreateIndex(
		ModNodeId firstNode,
		string firstPath,
		ModNodeId secondNode,
		string secondPath)
		=> new(
			DateTimeOffset.UtcNow,
			new Dictionary<ModNodeId, IReadOnlyList<IndexedPatchFile>>
			{
				[firstNode] = [new IndexedPatchFile(firstNode, firstPath, Path.GetFileName(firstPath), "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Base, new FileInfo(firstPath).Length, DateTimeOffset.UtcNow)],
				[secondNode] = [new IndexedPatchFile(secondNode, secondPath, Path.GetFileName(secondPath), "9ba626afa44a3aa3", 1, 1, PatchSidecarKind.Base, new FileInfo(secondPath).Length, DateTimeOffset.UtcNow)],
			},
			[]);

	private static string CreateScratch()
	{
		var directory = Path.Combine(Path.GetTempPath(), "hd2-loader-package-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static void TryDelete(string directory)
	{
		try { Directory.Delete(directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
