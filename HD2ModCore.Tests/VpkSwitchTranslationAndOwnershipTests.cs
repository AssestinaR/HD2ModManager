using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 开关表的 Unit 键翻译，以及"这个素体是谁的"。
/// <para>
/// 前者是换目标护甲之后开关还能不能用的唯一保证：掩码是 <c>(unit, section)</c>，
/// 而换甲会把 Unit 换到目标护甲的键上。后者决定"能不能单独删掉一个素体"。
/// </para>
/// </summary>
public sealed class VpkSwitchTranslationAndOwnershipTests : IDisposable
{
	private const string OldHead = "18054b9f5a540001";
	private const string OldChest = "18054b9f5a540002";
	private const string NewHead = "3c09e08493ee44b8";
	private const string NewChest = "cae35ee15168145f";

	private readonly string directory = Path.Combine(
		Path.GetTempPath(), "hd2-switch-translate-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	private static VpkComponentSwitchDocument Document() => new(
		VpkComponentSwitchDocument.CurrentSchema,
		[
			new VpkComponentSwitchEntry("猫耳", "a.vpk", ["c_white_a_ear"], [new VpkComponentSwitchTarget(OldHead, [3])]),
			new VpkComponentSwitchEntry("外套", "b.vpk", ["_03_coat"],
			[
				new VpkComponentSwitchTarget(OldHead, [5]),
				new VpkComponentSwitchTarget(OldChest, [1, 2]),
			]),
			new VpkComponentSwitchEntry("尾巴", "c.vpk", ["c_white_a_tail"], [new VpkComponentSwitchTarget("ffffffffffffffff", [0])]),
		],
		[]);

	[Fact]
	public void Translate_MovesEveryMaskOntoTheNewUnitKeys()
	{
		var translated = VpkComponentSwitchFile.Translate(Document(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			[OldHead] = NewHead,
			[OldChest] = NewChest,
		});

		var ears = translated.Switches.Single(item => item.Name == "猫耳");
		Assert.Equal(NewHead, Assert.Single(ears.Disabled).Unit);
		Assert.Equal([3], ears.Disabled[0].Sections);

		var coat = translated.Switches.Single(item => item.Name == "外套");
		Assert.Equal([NewHead, NewChest], coat.Disabled.Select(target => target.Unit).ToArray());
		Assert.Equal(3, coat.SectionCount);
	}

	/// <summary>
	/// 映射不到目标的开关<b>保留但清空</b>：名字与材质仍然有用（说明这个部件在新目标上没落点），
	/// 清空掩码让界面能明说"它现在无处可关"，而不是画一个点了没反应的开关。
	/// </summary>
	[Fact]
	public void Translate_KeepsSwitchesWithoutAMappingButClearsTheirMask()
	{
		var translated = VpkComponentSwitchFile.Translate(Document(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			[OldHead] = NewHead,
		});

		var tail = translated.Switches.Single(item => item.Name == "尾巴");
		Assert.Empty(tail.Disabled);
		Assert.Equal(0, tail.SectionCount);
		// 名字与材质必须还在：用户要能看到"这个开关没落点"，而不是它凭空消失。
		Assert.Equal("c_white_a_tail", Assert.Single(tail.Materials));
	}

	/// <summary>两个来源 Unit 映射到同一个目标 Unit 时，section 要并起来而不是丢掉一份。</summary>
	[Fact]
	public void Translate_MergesTargetsThatLandOnTheSameUnit()
	{
		var merged = new VpkComponentSwitchDocument(
			VpkComponentSwitchDocument.CurrentSchema,
			[new VpkComponentSwitchEntry("合并", "m.vpk", ["x"],
				[new VpkComponentSwitchTarget(OldHead, [1]), new VpkComponentSwitchTarget(OldChest, [4])])],
			[]);

		var translated = VpkComponentSwitchFile.Translate(merged, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			[OldHead] = NewHead,
			[OldChest] = NewHead,
		});

		var target = Assert.Single(translated.Switches[0].Disabled);
		Assert.Equal(NewHead, target.Unit);
		Assert.Equal([1, 4], target.Sections);
	}

	[Fact]
	public void Translate_WithAnEmptyMapClearsEverythingButKeepsTheSwitches()
	{
		var translated = VpkComponentSwitchFile.Translate(Document(), new Dictionary<string, string>());

		Assert.Equal(3, translated.Switches.Count);
		Assert.All(translated.Switches, entry => Assert.Empty(entry.Disabled));
	}

	[Fact]
	public void CustomModelFile_ReadsTheKeyAndIgnoresBrokenFiles()
	{
		Directory.CreateDirectory(directory);
		Assert.Null(VpkCustomModelFile.TryReadKey(directory));

		File.WriteAllText(Path.Combine(directory, VpkCustomModelFile.FileName),
			"{ \"key\": \"custom-abc\", \"name\": \"Manuka JABIS\" }");
		Assert.Equal("custom-abc", VpkCustomModelFile.TryReadKey(directory));

		File.WriteAllText(Path.Combine(directory, VpkCustomModelFile.FileName), "{ not json");
		Assert.Null(VpkCustomModelFile.TryReadKey(directory));
	}

	/// <summary>
	/// 扫库：只有带 <c>custom-model.json</c> 的 Mod 目录才算"持有"素体，并且要带出
	/// <b>持有它的 Mod 名</b>——拒绝删除时那句话要能点名，否则用户不知道该移除哪一个。
	/// </summary>
	[Fact]
	public void CustomModelFile_CollectsOwnersWithTheModName()
	{
		var owned = Path.Combine(directory, "vpk-mod-a");
		var plain = Path.Combine(directory, "hand-made");
		Directory.CreateDirectory(owned);
		Directory.CreateDirectory(plain);
		File.WriteAllText(Path.Combine(owned, VpkCustomModelFile.FileName),
			"{ \"key\": \"custom-a\", \"name\": \"MourningVeil Bill\" }");
		// 手搓的 Mod 没有这个文件——它里面若出现素体，那是用户自己的，不该被保护。
		File.WriteAllText(Path.Combine(plain, "vpk2patch.json"), "{}");

		var owners = VpkCustomModelFile.ReadOwners(directory);

		Assert.Equal("MourningVeil Bill", Assert.Single(owners).Value);
		Assert.Equal("custom-a", owners.Keys.Single());
	}

	/// <summary>没有 <c>name</c> 时退回目录名：两者在正常流程里是同一个值。</summary>
	[Fact]
	public void CustomModelFile_FallsBackToTheDirectoryName()
	{
		var owned = Path.Combine(directory, "manuka_jabis_zoey");
		Directory.CreateDirectory(owned);
		File.WriteAllText(Path.Combine(owned, VpkCustomModelFile.FileName), "{ \"key\": \"custom-b\" }");

		var owners = VpkCustomModelFile.ReadOwners(directory);

		Assert.Equal("manuka_jabis_zoey", Assert.Single(owners).Value);
	}
}
