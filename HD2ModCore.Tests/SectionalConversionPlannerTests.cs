using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「来源 → sectional」的规划规则：产出哪几组、每组装什么。
/// <para>
/// 这是整条 sectional 转换链里唯一"想清楚就能测"的部分——真正写 patch 的活依赖游戏数据，
/// 而组怎么分、名字从哪来、哪些 key 进哪一组，全在这里定死。
/// </para>
/// </summary>
public sealed class SectionalConversionPlannerTests
{
	private const ulong UnitType = 0x1111111111111111;
	private const ulong MaterialType = 0xeac0b497876adedf;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	private static readonly ulong[] UnitTypes = [UnitType];
	private static readonly ulong[] MaterialTypes = [MaterialType];
	private static readonly ulong[] TextureTypes = [TextureType];

	private static SectionalAssetRef Unit(ulong id) => new(UnitType, id);
	private static SectionalAssetRef Material(ulong id) => new(MaterialType, id);
	private static SectionalAssetRef Texture(ulong id) => new(TextureType, id);

	private static string Key(SectionalAssetRef key) => SectionalConversionPlanner.FormatKey(key);

	private static SectionalSourceDocument Document(
		IReadOnlyList<SectionalSourceEntry> sources,
		params SectionalAssetRef[] whitelist)
		=> new(
			SectionalSourceManifest.CurrentSchema,
			DateTimeOffset.UtcNow,
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "来源护甲"),
			null,
			sources,
			new SectionalSourceAssetKeys(
				whitelist.Where(key => key.TypeId == UnitType).Select(Key).ToArray(),
				whitelist.Where(key => key.TypeId == MaterialType).Select(Key).ToArray(),
				whitelist.Where(key => key.TypeId == TextureType).Select(Key).ToArray()));

	private static SectionalPatchContents Contents(params SectionalAssetRef[] keys)
		=> new(keys.ToHashSet());

	[Fact]
	public void 主来源同时产出unit与材质时_分出一个内置材质组()
	{
		var document = Document(
			[new SectionalSourceEntry("a", "主体Mod", 0, PatchFiles: ["main.patch_0"])],
			Unit(1), Material(10), Texture(20));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1), Material(10), Texture(20)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		Assert.Equal(2, plan.Patches.Count);
		Assert.Equal(SectionalPatchRole.Unit, plan.Patches[0].Role);
		Assert.Single(plan.Patches[0].Selections);
		Assert.Equal(SectionalPatchRole.Material, plan.Patches[1].Role);
		Assert.Equal(SectionalPatchManifest.BuiltInMaterialOptionName, plan.Patches[1].OptionName);
		Assert.Equal(2, plan.Patches[1].Selections.Count);
	}

	[Fact]
	public void 每个来源各出一条材质组_组名用来源名()
	{
		var document = Document(
			[
				new SectionalSourceEntry("a", "主体Mod", 0, PatchFiles: ["main.patch_0"]),
				new SectionalSourceEntry("b", "材质包A", 1, PatchFiles: ["pack-a.patch_1"]),
				new SectionalSourceEntry("c", "材质包B", 2, PatchFiles: ["pack-b.patch_2"]),
			],
			Unit(1), Material(10), Material(11), Material(12));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1)),
				["pack-a.patch_1"] = Contents(Material(11)),
				["pack-b.patch_2"] = Contents(Material(12)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		// unit + 两个材质组；主来源是纯 unit，不产空的材质组。
		Assert.Equal(3, plan.Patches.Count);
		Assert.Equal(["材质包A", "材质包B"], plan.Patches.Skip(1).Select(patch => patch.OptionName));
		Assert.All(plan.Patches.Skip(1), patch => Assert.NotNull(patch.SourceName));
	}

	[Fact]
	public void 补问一_同一条材质可以同时出现在内置组与另一个来源组()
	{
		// 用户口径：两类不互斥，同一条材质允许同时出现在"内置材质"与某个来源组里，
		// 在 sectional/ 里是两份不同的 patch。
		// 注意贡献过 unit 的那个 patch 不参与"来源组"（否则只是同一份内容被数两遍）；
		// 跨来源的重复才是这条要表达的东西。
		var document = Document(
			[
				new SectionalSourceEntry("a", "主体Mod", 0, PatchFiles: ["main.patch_0"]),
				new SectionalSourceEntry("b", "材质包A", 1, PatchFiles: ["pack-a.patch_1"]),
			],
			Unit(1), Material(10));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1), Material(10)),
				["pack-a.patch_1"] = Contents(Material(10)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		var materials = plan.Patches.Where(patch => patch.Role == SectionalPatchRole.Material).ToArray();
		Assert.Equal(2, materials.Length);
		Assert.Equal(SectionalPatchManifest.BuiltInMaterialOptionName, materials[0].OptionName);
		Assert.Equal("材质包A", materials[1].OptionName);
		Assert.All(materials, patch => Assert.Equal(Material(10), Assert.Single(patch.Selections).Key));
		Assert.Equal("main.patch_0", materials[0].Selections[0].SourcePatch);
		Assert.Equal("pack-a.patch_1", materials[1].Selections[0].SourcePatch);
	}

	[Fact]
	public void 白名单之外的key一律不收集()
	{
		var document = Document(
			[new SectionalSourceEntry("a", "主体Mod", 0, PatchFiles: ["main.patch_0"])],
			Unit(1));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1), Unit(2), Material(10)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		var unit = plan.Unit!;
		Assert.Equal(Unit(1), Assert.Single(unit.Selections).Key);
		Assert.Single(plan.Patches);   // 没有材质组
	}

	[Fact]
	public void 多个来源都有同一个unit时_后面的来源覆盖前面的()
	{
		var document = Document(
			[
				new SectionalSourceEntry("a", "旧来源", 0, PatchFiles: ["a.patch_0"]),
				new SectionalSourceEntry("b", "新来源", 1, PatchFiles: ["b.patch_0"]),
			],
			Unit(1));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["a.patch_0"] = Contents(Unit(1)),
				["b.patch_0"] = Contents(Unit(1)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		Assert.Equal("b.patch_0", Assert.Single(plan.Unit!.Selections).SourcePatch);
	}

	[Fact]
	public void 旧依据文件没有PatchFiles时_退化为共用全部patch()
	{
		var document = Document(
			[new SectionalSourceEntry("a", "主体Mod", 0)],
			Unit(1), Material(10));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1), Material(10)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		Assert.Equal(2, plan.Patches.Count);   // unit + 内置材质
	}

	[Fact]
	public void 来源里没有白名单unit时给出警告()
	{
		var document = Document(
			[new SectionalSourceEntry("a", "主体Mod", 0, PatchFiles: ["main.patch_0"])],
			Unit(9), Material(10));

		var plan = SectionalConversionPlanner.Plan(
			document,
			new Dictionary<string, SectionalPatchContents>
			{
				["main.patch_0"] = Contents(Unit(1), Material(10)),
			},
			UnitTypes, MaterialTypes, TextureTypes);

		Assert.Empty(plan.Unit!.Selections);
		Assert.NotEmpty(plan.Warnings);
	}

	[Theory]
	[InlineData(0xeac0b497876adedfUL, 0x0011223344556677UL)]
	[InlineData(1UL, 2UL)]
	public void 白名单key的文本形式可以往返(ulong typeId, ulong fileId)
	{
		var key = new SectionalAssetRef(typeId, fileId);

		Assert.True(SectionalConversionPlanner.TryParseKey(SectionalConversionPlanner.FormatKey(key), out var parsed));
		Assert.Equal(key, parsed);
	}

	[Theory]
	[InlineData("")]
	[InlineData("没有冒号")]
	[InlineData("zz:11")]
	[InlineData(":")]
	public void 非法key文本被拒绝(string raw)
	{
		Assert.False(SectionalConversionPlanner.TryParseKey(raw, out _));
	}
}
