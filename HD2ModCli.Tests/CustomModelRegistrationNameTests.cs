using HD2ModCore.Domain;
using Xunit;
using static HD2ModCli.CliApplication;

namespace HD2ModCli.Tests;

/// <summary>
/// 重新登记素体时用什么名字。
/// <para>
/// 这条判据治的是用户报的"改名过一会又被还原"：护甲映射页允许给素体改名，而重跑转换
/// （重新导入同一个包、重构建产物）拿到的是 VPK 文件名。谁赢决定了用户的修改能不能活过
/// 一次后台重构建。结论是<b>工作区里已有的名字赢</b>——那是唯一由用户亲手写下的值。
/// </para>
/// </summary>
public sealed class CustomModelRegistrationNameTests
{
	private static RuntimeArmorRegistryModel Model(string key, string? name)
		=> new(key, null, [new RuntimeArmorRegistryModelPiece(3, "0123456789abcdef", 2, 0)], name);

	[Fact]
	public void AnExistingNameSurvivesReregistration()
	{
		var existing = new[] { Model("custom-a", "夜会 夏安（我改的名字）") };

		var name = ResolveRegisteredModelName("[1人物]夏安-青涩夜会.vpk", existing, "custom-a");

		Assert.Equal("夜会 夏安（我改的名字）", name);
	}

	[Fact]
	public void AFirstRegistrationUsesTheIncomingName()
	{
		var name = ResolveRegisteredModelName("[1人物]夏安.vpk", [], "custom-a");

		Assert.Equal("[1人物]夏安.vpk", name);
	}

	/// <summary>
	/// 老工作区里可能有一条 <c>name</c> 为空的记录（协议回退、早期版本写的）。
	/// 那种情况没有"用户改过的名字"可保，照常用本次的名字 —— 否则素体在界面上永远显示成 key。
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void AnEmptyExistingNameFallsBackToTheIncomingOne(string? existingName)
	{
		var existing = new[] { Model("custom-a", existingName) };

		var name = ResolveRegisteredModelName("[1人物]夏安.vpk", existing, "custom-a");

		Assert.Equal("[1人物]夏安.vpk", name);
	}

	/// <summary>只认这个 key：别的素体的名字不该被搬过来。</summary>
	[Fact]
	public void AnotherModelsNameIsNotBorrowed()
	{
		var existing = new[] { Model("custom-a", "别人"), Model("custom-b", "另一个") };

		var name = ResolveRegisteredModelName("新名字", existing, "custom-c");

		Assert.Equal("新名字", name);
	}
}
