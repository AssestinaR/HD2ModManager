using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// **只有来源 VPK 的目录也是合法条目。**
/// <para>
/// 新导入链路的形状是「解压 → 建一个只含来源 VPK 的条目 → 自动跑一次标准重构建」：
/// 转换与编译都由重构建产出，导入阶段不该要求 <c>sectional/</c> 或 patch 已经存在。
/// 以前导入器在第一步就判"没有可导入内容"，这条链路根本走不到重构建。
/// </para>
/// </summary>
public sealed class SourceOnlyModImportTests
{
	[Fact]
	public async Task ImportFolderAsync_AcceptsAFolderThatOnlyHasSourceVpk()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var source = Path.Combine(root, "source");
		Directory.CreateDirectory(source);
		Directory.CreateDirectory(appRoot);
		try
		{
			// 内容不重要：导入器只把来源当作权威事实搬进 mod 根。
			File.WriteAllBytes(Path.Combine(source, "Lunalice_Bill.vpk"), [0x01, 0x02, 0x03]);
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			var node = Assert.Single(result.Snapshot.Nodes.Values);
			Assert.Empty(node.PatchGroups);
			var modDirectory = Path.Combine(paths.ModsDirectory, node.RelativePath);
			Assert.True(File.Exists(Path.Combine(modDirectory, "Lunalice_Bill.vpk")));

			// 类型判定必须已经认得出来：有来源 VPK ⇒ vpk2patch 类（重构建入口靠它）。
			Assert.Equal(
				ModStorageKind.Vpk2Patch,
				new ModStorageKindResolver(new PatchFileNameParser()).Resolve(modDirectory));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}
}
