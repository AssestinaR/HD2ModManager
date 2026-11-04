using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// section 组模型的契约：**旧文件不用迁移就能读**、**id 在改名后不变**、**互斥与查找按 id 走**。
/// <para>
/// 这一组测试守的是"组表能不能当主干"这件事本身：只要 id 会随改名变，界面的选中态、
/// 互斥关系、将来任何按组存的东西就都会错位；只要旧文件读不进来，库里 9 个已有 Mod
/// 就会突然"没有组"。
/// </para>
/// </summary>
public sealed class SectionGroupModelTests : IDisposable
{
	private const string UnitA = "18054b9f5a540001";
	private const string UnitB = "18054b9f5a540002";

	private readonly string directory = Path.Combine(
		Path.GetTempPath(), "hd2-group-" + Guid.NewGuid().ToString("N"));

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

	/// <summary>库里真实存在的旧格式（"组件开关"时代）。<c>sectionCount</c> 是当年一起写进去的算出来的属性。</summary>
	private const string LegacyJson = """
	{
	  "schema": "hd2mod.vpk-components/1",
	  "switches": [
	    {
	      "name": "猫耳",
	      "sourceVpk": "Manuka_JABIS猫耳关.vpk",
	      "materials": ["c_white_a_ear"],
	      "disabled": [ { "unit": "18054B9F5A540001", "sections": [3] } ],
	      "sectionCount": 1
	    }
	  ],
	  "unmatched": [
	    { "name": "文身", "sourceVpk": "Manuka_JABIS文身关.vpk", "reason": "它换的是贴图。" }
	  ]
	}
	""";

	[Fact]
	public void LegacyFile_ReadsInAsNoDrawGroupsWithoutMigration()
	{
		var parsed = SectionGroupFile.Parse(LegacyJson);

		// 读进来就是当前格式：下次保存自动升级，不需要迁移脚本。
		Assert.Equal(SectionGroupDocument.CurrentSchema, parsed.Schema);
		var group = Assert.Single(parsed.Groups);
		Assert.Equal("猫耳", group.Name);
		Assert.Equal(SectionGroupSource.NoDraw, group.Source);
		Assert.Null(group.ExclusiveGroup);
		Assert.Equal(1, group.SectionCount);

		// unit 键要归一成小写——旧文件里手写成大写也是合法的。
		Assert.Equal(UnitA, group.Members[0].Unit);
		Assert.Equal([3], group.Members[0].Sections);

		// 旧文件没有 id 字段，但必须能给出一个稳定 id（否则界面选中态无从下手）。
		Assert.Empty(group.Id);
		Assert.StartsWith(SectionGroupIdentity.Prefix, group.EffectiveId, StringComparison.Ordinal);
		Assert.Single(parsed.Unmatched);
	}

	[Fact]
	public void Serialize_WritesTheNewKeyNamesAndEnumAsText()
	{
		var text = SectionGroupFile.Serialize(SectionGroupFileTests.CreateDocument());

		Assert.Contains("\"groups\"", text, StringComparison.Ordinal);
		Assert.Contains("\"members\"", text, StringComparison.Ordinal);
		Assert.Contains("\"source\": \"noDraw\"", text, StringComparison.Ordinal);
		// 旧键名一个都不该再出现（否则读的人分不清哪套是真的）。
		Assert.DoesNotContain("\"switches\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\"disabled\"", text, StringComparison.Ordinal);
		// 算出来的属性不该进文件。
		Assert.DoesNotContain("sectionCount", text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("effectiveId", text, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Save_StampsTheIdSoRenamingDoesNotChangeIt()
	{
		await SectionGroupFile.WriteAsync(directory, SectionGroupFileTests.CreateDocument());
		var first = await SectionGroupFile.TryReadAsync(directory);
		Assert.NotNull(first);
		var idBefore = first!.Groups[0].Id;
		Assert.False(string.IsNullOrEmpty(idBefore));

		// 改名后重新落盘：id 必须还是原来那个，否则"按 id 记住的东西"全部错位。
		var renamed = first with
		{
			Groups = [first.Groups[0] with { Name = "猫耳（改过名）" }, first.Groups[1]],
		};
		await SectionGroupFile.WriteAsync(directory, renamed);
		var second = await SectionGroupFile.TryReadAsync(directory);

		Assert.NotNull(second);
		Assert.Equal(idBefore, second!.Groups[0].Id);
		Assert.Equal("猫耳（改过名）", second.Groups[0].Name);
	}

	[Fact]
	public void Derive_IsOrderInsensitiveAndContentSensitive()
	{
		SectionGroupTarget[] forward = [new(UnitA, [3]), new(UnitB, [1, 2])];
		SectionGroupTarget[] reversed = [new(UnitB, [1, 2]), new(UnitA, [3])];

		Assert.Equal(SectionGroupIdentity.Derive("外套", forward), SectionGroupIdentity.Derive("外套", reversed));
		Assert.NotEqual(SectionGroupIdentity.Derive("外套", forward), SectionGroupIdentity.Derive("外套", [new(UnitA, [3])]));
		Assert.NotEqual(SectionGroupIdentity.Derive("外套", forward), SectionGroupIdentity.Derive("外套2", forward));
	}

	[Fact]
	public void FindById_UsesTheEffectiveId()
	{
		var document = SectionGroupFile.Parse(LegacyJson);
		var group = document.Groups[0];

		Assert.Same(group, document.FindById(group.EffectiveId));
		Assert.Null(document.FindById("g-does-not-exist"));
	}

	[Fact]
	public void EnabledInExclusiveGroup_ReturnsOnlyEnabledMembersOfThatGroup()
	{
		var document = new SectionGroupDocument(
			SectionGroupDocument.CurrentSchema,
			[
				new SectionGroup("发色 白", "", [], [new SectionGroupTarget(UnitA, [1])]) { ExclusiveGroup = "发色" },
				new SectionGroup("发色 黑", "", [], [new SectionGroupTarget(UnitA, [2])]) { ExclusiveGroup = "发色" },
				new SectionGroup("瞳色 紫", "", [], [new SectionGroupTarget(UnitA, [3])]) { ExclusiveGroup = "瞳色" },
				new SectionGroup("猫耳", "", [], [new SectionGroupTarget(UnitA, [4])]),
			],
			[]);

		var enabled = document.EnabledInExclusiveGroup("发色", group => group.Name != "发色 黑");

		var single = Assert.Single(enabled);
		Assert.Equal("发色 白", single.Name);
		Assert.Empty(document.EnabledInExclusiveGroup("不存在的组", _ => true));
	}

	[Fact]
	public void Translate_ClearsMembersWhenNothingMaps()
	{
		var document = SectionGroupFileTests.CreateDocument();

		var translated = SectionGroupFile.Translate(document, new Dictionary<string, string>());

		Assert.All(translated.Groups, group => Assert.Empty(group.Members));
		// 名字与材质必须留住：界面还要靠它说"这个部件在当前目标上没有落点"。
		Assert.Equal(document.Groups[0].Name, translated.Groups[0].Name);
		Assert.Equal(document.Groups[0].Materials, translated.Groups[0].Materials);
	}
}
