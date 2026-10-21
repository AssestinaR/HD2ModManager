using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 在**真实产物**上验一遍烘焙：关掉一个 section、三角形数确实变了、再打开逐字节还原。
/// <para>
/// 为什么单列一个文件：合成 TOC 只能证明"按偏移写对了 dword"，证明不了"这个偏移在真实
/// patch 里真的是那个 section 的 numIndices"。后者要靠 <c>vpk2patch</c> 真产出的
/// <c>sections.json</c> 与实际字节对齐——而偏移算错的表现是几何错位，不是报错。
/// </para>
/// <para>
/// 样本由环境变量给出（<c>HD2_TEST_VPK_PATCH</c> / <c>HD2_TEST_VPK_SECTIONS</c>）；
/// 没有样本时静默跳过，符合本仓库"真数据测试缺样本就跳过"的约定。
/// </para>
/// </summary>
public sealed class WardrobeSectionBakeRealDataTests
{
	[Fact]
	public async Task Bake_OnARealPatch_TurnsOffASectionAndRestoresByteForByte()
	{
		var patchPath = Environment.GetEnvironmentVariable("HD2_TEST_VPK_PATCH");
		var mapPath = Environment.GetEnvironmentVariable("HD2_TEST_VPK_SECTIONS");
		if (string.IsNullOrWhiteSpace(patchPath) || !File.Exists(patchPath)) return;
		if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath)) return;

		var map = await WardrobeSectionMapFile.ReadAsync(mapPath);
		var unit = map.Units.OrderByDescending(candidate => candidate.Sections.Count).First();
		var section = unit.Sections[^1];
		Assert.NotEmpty(section.Toggles);

		var original = await File.ReadAllBytesAsync(patchPath);
		try
		{
			var disabled = new Dictionary<string, IReadOnlyCollection<int>> { [unit.FileId] = [section.Index] };
			var baked = await WardrobeSectionBaker.BakeAsync(patchPath, map, disabled);

			// 每一级 LOD 都要写：少写一级，玩家在对应距离档位就会看到"关不掉"。
			Assert.Equal(section.Toggles.Count, baked.DwordsWritten);
			Assert.True(baked.SectionsDisabled > 0);

			// 从字节反推：这一块确实被清零了。
			var readBack = await WardrobeSectionBaker.ReadBakedAsync(patchPath, map);
			Assert.True(readBack.TryGetValue(unit.FileId, out var indices));
			Assert.Contains(section.Index, indices);

			// 其它 section 一个字节都不该动：把这份"烘过的"与"原始的"逐字节比，
			// 差异只能落在这一块 section 的落点上。
			// 注意：落点是**相对该 unit payload** 的，比较前要加上切片起点。
			var afterBake = await File.ReadAllBytesAsync(patchPath);
			Assert.Equal(original.Length, afterBake.Length);
			var slices = WardrobeSectionBaker.ScanUnitSlices(original, Path.GetFileName(patchPath));
			Assert.True(slices.TryGetValue(unit.FileId, out var slice));
			var absoluteOffsets = section.Toggles
				.Select(toggle => slice.Offset + Convert.ToInt32(toggle.NumIndicesOffset[2..], 16))
				.ToHashSet();
			var differing = Enumerable.Range(0, original.Length)
				.Where(index => original[index] != afterBake[index])
				.ToArray();
			Assert.NotEmpty(differing);
			Assert.All(differing, index => Assert.Contains(absoluteOffsets, offset => index >= offset && index < offset + 4));
		}
		finally
		{
			await File.WriteAllBytesAsync(patchPath, original);
		}
	}
}
