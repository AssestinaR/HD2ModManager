using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 编译目标只能是"这个 Mod 自己的素体"。
/// <para>
/// sectional / vpk2patch 的产物**本身就是一具自定义素体**：换到别人的素体上会让产物去改别人的
/// unit 键（宿主与注册表分家），换到游戏护甲上则是"把原版护甲件全部隐藏"的另一个决定。
/// 归一必须落在编译这一层——界面藏选项只能挡住一条路，而 <c>wardrobe.json</c> 里可能留着旧目标，
/// 「切换 / 应用 / 重构建 / 批量重构建」四条路都经过这里。
/// </para>
/// </summary>
public sealed class ModCompileTargetTests
{
	private const string OwnModel = "custom-model:custom-db02a9c55d5e4a44a231311c6b6432b8";
	private const string GameArmor = "1e2ba3712fa1b030";
	private const string SomeoneElsesModel = "custom-model:custom-09846457fdbf4d67894d48ef1e1549da";

	[Fact]
	public void Normalize_KeepsTheOwnModelWhenThatIsAlreadyTheTarget()
	{
		var target = ModCompileService.NormalizeTargetArchiveId(OwnModel, OwnModel, out var normalized);

		Assert.Equal(OwnModel, target);
		Assert.False(normalized);
	}

	[Fact]
	public void Normalize_ReplacesAGameArmorWithTheOwnModel()
	{
		var target = ModCompileService.NormalizeTargetArchiveId(GameArmor, OwnModel, out var normalized);

		Assert.Equal(OwnModel, target);
		Assert.True(normalized);
	}

	[Fact]
	public void Normalize_ReplacesSomeoneElsesModelWithTheOwnModel()
	{
		var target = ModCompileService.NormalizeTargetArchiveId(SomeoneElsesModel, OwnModel, out var normalized);

		Assert.Equal(OwnModel, target);
		Assert.True(normalized);
	}

	[Fact]
	public void Normalize_FallsBackToTheOwnModelWhenNothingWasRecorded()
	{
		var target = ModCompileService.NormalizeTargetArchiveId(requested: null, OwnModel, out var normalized);

		Assert.Equal(OwnModel, target);
		Assert.True(normalized);
	}

	[Fact]
	public void Normalize_LeavesTheRequestAloneWhenTheModHasNoOwnModel()
	{
		// 材质包这类没有自己的素体：沿用记录（通常就是 null），不要去猜一个目标。
		var target = ModCompileService.NormalizeTargetArchiveId(GameArmor, ownArchiveId: null, out var normalized);

		Assert.Equal(GameArmor, target);
		Assert.False(normalized);
	}
}
