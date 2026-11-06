using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「按材质 id 在库里找提供者」这条查询（「生成sectional版」靠它把配套材质一起收进来）。
/// <para>
/// 三个真机上验证过的要点：
/// ① 只有 <c>emitted: true</c> 的条目才算"提供"——否则模型会把自己列成提供者；
/// ② 发起转换的那个 Mod 自己要被排除；
/// ③ 找不到的 id 要如实报出来（真库里每个模型都差同一个共享材质 id，
///    那是引擎自带的，不该被当成缺包）。
/// </para>
/// </summary>
public sealed class MaterialSourceCollectorTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-material-sources-" + Guid.NewGuid().ToString("N"));

	public MaterialSourceCollectorTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	/// <summary>造一个 Mod：报告里列出若干 (id, emitted) 明细，根上放一个同名 vpk。</summary>
	private string NewMod(string name, bool hasUnits, params (ulong Id, bool Emitted)[] materials)
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name + ".vpk"), "vpk");

		var details = string.Join(",", materials.Select((item, index) =>
			$"{{\"index\":{index},\"name\":\"m{index}\",\"material\":\"0x{item.Id:x16}\",\"emitted\":{item.Emitted.ToString().ToLowerInvariant()}}}"));
		var json = $$"""
		{
		  "ok": true,
		  "idNamespace": "vpk-test",
		  "patches": [
		    {
		      "name": "{{name}}.vpk",
		      "kind": "{{(hasUnits ? "model" : "material")}}",
		      "units": {{(hasUnits ? 7 : 0)}},
		      "materials": {{materials.Length}},
		      "textures": 0,
		      "materialDetails": [{{details}}]
		    }
		  ]
		}
		""";
		File.WriteAllText(Path.Combine(directory, "vpk2patch.json"), json);
		return directory;
	}

	[Fact]
	public void Collect_FindsTheModThatEmitsTheMaterial()
	{
		var provider = NewMod("材质包A", hasUnits: false, (0x1111, true));
		NewMod("别的包", hasUnits: false, (0x2222, true));

		var collection = MaterialSourceCollector.Collect(_root, [0x1111]);

		var candidate = Assert.Single(collection.Candidates);
		Assert.Equal(Path.GetFullPath(provider), Path.GetFullPath(candidate.Directory));
		Assert.True(candidate.IsPureMaterial);
		Assert.Single(candidate.SourceVpkPaths);
		Assert.Empty(collection.UnresolvedMaterialIds);
	}

	// 真机上的坑：模型的报告里有一堆 emitted:false 的材质明细（它引用了，但没写出来）。
	// 把它们算成提供者，模型就会"提供自己的材质"。
	[Fact]
	public void Collect_IgnoresMaterialsThatWereOnlyReferenced()
	{
		NewMod("模型", hasUnits: true, (0x1111, false), (0x3333, true));

		var collection = MaterialSourceCollector.Collect(_root, [0x1111, 0x3333]);

		// 0x3333 是它确实写出来的，所以它算提供者；0x1111 只是引用，不算。
		var candidate = Assert.Single(collection.Candidates);
		Assert.Equal([0x3333UL], candidate.MatchedMaterialIds);
		Assert.Equal([0x1111UL], collection.UnresolvedMaterialIds);
	}

	[Fact]
	public void Collect_SkipsTheExcludedDirectory()
	{
		var self = NewMod("自己", hasUnits: true, (0x1111, true));

		var collection = MaterialSourceCollector.Collect(_root, [0x1111], self);

		Assert.Empty(collection.Candidates);
		Assert.Equal([0x1111UL], collection.UnresolvedMaterialIds);
	}

	[Fact]
	public void Collect_ReportsUnresolvedIds_AndOrdersByHitCount()
	{
		NewMod("小包", hasUnits: false, (0x1111, true));
		NewMod("大包", hasUnits: false, (0x1111, true), (0x2222, true));

		var collection = MaterialSourceCollector.Collect(_root, [0x1111, 0x2222, 0x9999]);

		Assert.Equal(2, collection.Candidates.Count);
		Assert.Equal("大包", collection.Candidates[0].DisplayName);
		Assert.Equal([0x9999UL], collection.UnresolvedMaterialIds);
		Assert.Equal(2, collection.SourceVpkPaths.Count);
	}

	[Fact]
	public void Collect_OnEmptyInput_ReturnsNothing()
		=> Assert.Empty(MaterialSourceCollector.Collect(_root, []).Candidates);
}
