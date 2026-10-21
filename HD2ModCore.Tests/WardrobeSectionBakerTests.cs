using System.Buffers.Binary;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// section 烘焙：把"哪些 section 不画"直接写进 patch 里的 unit。
/// <para>
/// 这里要守住的不是"能写"，而是三条容易悄悄坏掉的约束：
/// </para>
/// <list type="number">
/// <item><b>可逆</b>——关掉再打开必须逐字节回到原样。原始 <c>numIndices</c> 只存在
/// <c>sections.json</c> 里，一旦写丢就永远回不去。</item>
/// <item><b>幂等</b>——重复烘焙同一份禁用集合不能反复写盘（部署前每次保存都会调一次）。</item>
/// <item><b>对不上就拒绝</b>——偏移指向的值既不是 0 也不是记录里的原值时，说明这份
/// <c>sections.json</c> 与这份 patch 不是一套。此时写下去会改到别的字段上，
/// 产出"看起来正常、几何却错位"的 patch，比直接报错难查得多。</item>
/// </list>
/// </summary>
public sealed class WardrobeSectionBakerTests
{
	private const int TocLength = 0x4000;

	private static string CreateToc(WardrobeSectionMapDocument map)
	{
		var bytes = new byte[TocLength];
		// 先铺成"未烘焙"：每个落点都写成记录里的原始 numIndices。
		foreach (var unit in map.Units)
		{
			foreach (var section in unit.Sections)
			{
				foreach (var toggle in section.Toggles)
				{
					var offset = Convert.ToInt32(toggle.NumIndicesOffset[2..], 16);
					BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), toggle.NumIndices);
				}
			}
		}

		var path = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N") + ".patch_0");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private static uint Read(string path, string hexOffset)
		=> BinaryPrimitives.ReadUInt32LittleEndian(
			File.ReadAllBytes(path).AsSpan(Convert.ToInt32(hexOffset[2..], 16), 4));

	[Fact]
	public async Task Bake_DisablesOnlyTheRequestedSections_AndRestoresExactly()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var path = CreateToc(map);
		var original = await File.ReadAllBytesAsync(path);
		try
		{
			// UnitA 的第 0 个 section 有两级 LOD，两级都必须清零——只写一级，
			// 玩家在对应距离档位上就会看到"关不掉"。
			var result = await WardrobeSectionBaker.BakeAsync(path, map,
				new Dictionary<string, IReadOnlyCollection<int>> { ["af431352d9b95e83"] = [0] });

			Assert.Equal(2, result.DwordsWritten);
			Assert.Equal(0u, Read(path, "0x00002010"));
			Assert.Equal(0u, Read(path, "0x00002800"));
			// 别的 section 一个字节都不该动。
			Assert.Equal(3072u, Read(path, "0x00002020"));
			Assert.Equal(2466u, Read(path, "0x00003010"));

			// 再打开：必须逐字节回到原样。
			var restored = await WardrobeSectionBaker.BakeAsync(path, map,
				new Dictionary<string, IReadOnlyCollection<int>>());
			Assert.Equal(2, restored.DwordsWritten);
			Assert.Equal(original, await File.ReadAllBytesAsync(path));
		}
		finally
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task Bake_IsIdempotent()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var path = CreateToc(map);
		try
		{
			var disabled = new Dictionary<string, IReadOnlyCollection<int>> { ["cae35ee15168145f"] = [0] };
			var first = await WardrobeSectionBaker.BakeAsync(path, map, disabled);
			var second = await WardrobeSectionBaker.BakeAsync(path, map, disabled);

			Assert.Equal(1, first.DwordsWritten);
			Assert.Equal(0, second.DwordsWritten);
			Assert.False(second.Changed);
		}
		finally
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task Bake_RejectsAPatchThatDoesNotMatchTheMap()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var path = CreateToc(map);
		try
		{
			// 有人重新转换过：同一个落点上现在是别的值。
			var bytes = await File.ReadAllBytesAsync(path);
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2010, 4), 12345);
			await File.WriteAllBytesAsync(path, bytes);

			var exception = await Assert.ThrowsAsync<InvalidDataException>(()
				=> WardrobeSectionBaker.BakeAsync(path, map,
					new Dictionary<string, IReadOnlyCollection<int>> { ["af431352d9b95e83"] = [0] }));
			Assert.Contains("对不上", exception.Message, StringComparison.Ordinal);
		}
		finally
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task ReadBaked_ReportsWhatThePatchActuallyHides()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var path = CreateToc(map);
		try
		{
			await WardrobeSectionBaker.BakeAsync(path, map,
				new Dictionary<string, IReadOnlyCollection<int>> { ["af431352d9b95e83"] = [1] });

			var baked = await WardrobeSectionBaker.ReadBakedAsync(path, map);

			Assert.Equal(new[] { 1 }, baked["af431352d9b95e83"]);
			Assert.DoesNotContain("cae35ee15168145f", baked.Keys);
		}
		finally
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task Bake_WarnsAboutUnitsAndSectionsThatDoNotExist()
	{
		var map = WardrobeSectionMapTests.CreateMap();
		var path = CreateToc(map);
		try
		{
			var result = await WardrobeSectionBaker.BakeAsync(path, map,
				new Dictionary<string, IReadOnlyCollection<int>>
				{
					["ffffffffffffffff"] = [0],
					["cae35ee15168145f"] = [7],
				});

			Assert.Equal(2, result.Warnings.Count);
			Assert.Contains(result.Warnings, warning => warning.Contains("ffffffffffffffff", StringComparison.Ordinal));
			Assert.Contains(result.Warnings, warning => warning.Contains("第 7 个", StringComparison.Ordinal));
		}
		finally
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}
}
