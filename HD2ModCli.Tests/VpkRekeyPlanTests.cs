using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 换甲映射（<see cref="CliVpkRekey.BuildPlan"/>）的层规则：**外层优先**。
/// <para>
/// 历史一：AF-52「禁锢」的右臂只有护甲层、没有内衣层，更早的规则把护甲层一律判成"隐藏"，
/// 于是原版右臂被隐藏、我们的右臂又没写出去 → "衣服还在、人体胳膊不见了"。
/// </para>
/// <para>
/// 历史二（现行规则）：内层优先会丢手套与丝袜。护甲件的 <c>mesh0</c> 是只写深度的剔除
/// 代理网格（形状就是整条前臂 / 小腿），而隐藏空壳把它原样保留，于是挂在更内层的几何
/// 被深度剔除。改成外层优先后实机正常。
/// </para>
/// </summary>
public sealed class VpkRekeyPlanTests
{
    private const string ArmorArchive = "aaaaaaaaaaaaaaaa";

    private const string HelmetArchive = "bbbbbbbbbbbbbbbb";

    [Fact]
    public void BuildPlan_MapsTheArmorLayerWhenThePartHasNoUndergarmentUnit()
    {
        // 目标只有护甲层的右臂（AF-52 的形状）。
        var target = Target(
            Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Slim),
            Part(0x08, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Stocky),
            Part(0x09, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));

        var (plan, notes, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        var mapped = plan.Mappings.Where(mapping => mapping.Source is not null).ToArray();
        Assert.Equal(3, mapped.Length);
        Assert.All(mapped, mapping => Assert.Equal(UnitMeshPartLayer.Armor, mapping.Target.Layer));
        Assert.All(mapped, mapping => Assert.True(mapping.WillReplace));
        Assert.Contains(notes, note => note.Contains("RightArm", StringComparison.Ordinal)
            && note.Contains("Armor 层承载", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPlan_PrefersTheArmorLayerAndLeavesTheUndergarmentToHiding()
    {
        // 目标内衣层与护甲层都有右臂（RE-2310 的形状）：几何换到**护甲层**，
        // 内衣层交给隐藏——内层优先会让手套 / 丝袜被外层护甲件的剔除代理按深度挡掉。
        var target = Target(
            Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Slim),
            Part(0x08, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Stocky),
            Part(0x09, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));

        var (plan, notes, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        var mapped = plan.Mappings.Where(mapping => mapping.Source is not null).ToArray();
        var mapping = Assert.Single(mapped);
        Assert.Equal(UnitMeshPartLayer.Armor, mapping.Target.Layer);
        Assert.Contains(notes, note => note.Contains("RightArm", StringComparison.Ordinal)
            && note.Contains("Armor 层承载", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPlan_PrefersTheAccessoryLayerWhenItExists()
    {
        // 三层都在时取**最外层**（Accessory）——"替换尽可能的外层、把内层隐藏"。
        var target = Target(
            Part(0x07, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Slim),
            Part(0x08, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Slim),
            Part(0x09, UnitMeshPartKind.Torso, UnitMeshPartLayer.Accessory, UnitMeshBodyVariant.Slim));

        var (plan, _, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        var mapping = Assert.Single(plan.Mappings, item => item.Source is not null);
        Assert.Equal(UnitMeshPartLayer.Accessory, mapping.Target.Layer);
    }

    [Fact]
    public void BuildPlan_FallsBackToTheAccessoryLayerWhenNeitherUndergarmentNorArmorExists()
    {
        var target = Target(Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Accessory, UnitMeshBodyVariant.Any));

        var (plan, _, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        var mapping = Assert.Single(plan.Mappings, item => item.Source is not null);
        Assert.Equal(UnitMeshPartLayer.Accessory, mapping.Target.Layer);
    }

    [Fact]
    public void BuildPlan_NeverUsesCullingUnitsAsCarriers()
    {
        // Culling 是隐藏壳：写进我们的几何只会得到"看不见的模型"。
        var target = Target(
            Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Culling, UnitMeshBodyVariant.Any),
            Part(0x08, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));

        var (plan, _, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        var mapping = Assert.Single(plan.Mappings, item => item.Source is not null);
        Assert.Equal(UnitMeshPartLayer.Armor, mapping.Target.Layer);
    }

    [Fact]
    public void BuildPlan_ReportsASourceUnitThatHasNoLandingSpotAtAll()
    {
        // 目标完全没有左臂：来源的左臂 Unit 会整份消失，必须说出来。
        var target = Target(Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));

        var (_, notes, _) = CliVpkRekey.BuildPlan(Source(), [target]);

        Assert.Contains(notes, note => note.Contains("LeftArm", StringComparison.Ordinal)
            && note.Contains("找不到", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPlan_DoesNotReportAMissingLandingSpotWhenTheUnitLivesInAnotherArchive()
    {
        // 换一件护甲同时给护甲与头盔两个归档：头盔 Unit 落在头盔归档里，
        // 不能因为"护甲归档里没有头盔"就报它没有落点。
        var armor = Target(
            Part(0x02, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Stocky),
            Part(0x03, UnitMeshPartKind.Hips, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Stocky),
            Part(0x04, UnitMeshPartKind.LeftLeg, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x05, UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x06, UnitMeshPartKind.LeftArm, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));
        var helmet = new EquipmentUnitCatalogEntry(HelmetArchive, "Helmet", "测试头盔",
            [Part(0x01, UnitMeshPartKind.Helmet, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any)]);

        var (plan, notes, _) = CliVpkRekey.BuildPlan(Source(), [armor, helmet]);

        Assert.Equal(7, plan.Mappings.Count(mapping => mapping.Source is not null));
        Assert.DoesNotContain(notes, note => note.Contains("找不到", StringComparison.Ordinal));
    }

    // ── 头身合并（2026-11-02）──
    //
    // 目的：头跟着护甲走，那件头盔 item 变成没有模型，玩家不必再单独穿它才有头。
    // 层号一律按目标护甲胸部槽位**实际有几层**算出来，不写死。
    //
    // 第三个返回值是**实际发生了什么**（不是"开关开没开"）：界面照它说话，
    // 所以这里逐个状态都验一遍——报错误的状态等于让界面撒谎。

    [Fact]
    public void Merge_PutsTheHeadOnTheOutermostTorsoLayerAndTheBodyBelowIt()
    {
        var (plan, notes, headMerge) = CliVpkRekey.BuildPlan(Source(), [ThreeLayerArmour(), HelmetKit()]);

        Assert.Equal(VpkHeadMergeState.Applied, headMerge);

        // 头 → 最外层（Accessory）；身体 → 次外层（Armor）；内衣层（Undergarment）留给隐藏。
        var head = Assert.Single(plan.Mappings, item =>
            item.Source?.PartKind == UnitMeshPartKind.Helmet);
        Assert.Equal(UnitMeshPartLayer.Accessory, head.Target.Layer);
        Assert.Equal(UnitMeshPartKind.Torso, head.Target.PartKind);

        var body = Assert.Single(plan.Mappings, item =>
            item.Source?.PartKind == UnitMeshPartKind.Torso);
        Assert.Equal(UnitMeshPartLayer.Armor, body.Target.Layer);

        // 内衣层那件没有被占用（会由 AutoHideUnmappedTargetUnits 隐藏）。
        Assert.DoesNotContain(plan.Mappings, item =>
            item.Target.Layer == UnitMeshPartLayer.Undergarment
            && item.Target.PartKind == UnitMeshPartKind.Torso
            && item.Source is not null);

        // 头盔套件一件都不写。
        Assert.DoesNotContain(plan.Mappings, item =>
            item.Target.SemanticName == "Helmet" && item.Source is not null);

        Assert.Contains(notes, note => note.Contains("头身合并", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_AdaptsToAnArmourWhoseTorsoSlotHasOnlyTwoLayers()
    {
        // 有的护甲胸部只有内衣 + 护甲两层：那就身体 → 内衣、头 → 护甲，仍然各自有位置。
        var armour = Target(
            Part(0x02, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x03, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));
        armour = armour with { ArchiveId = ArmorArchive, Category = "Armor", DisplayName = "两层护甲" };

        var (plan, _, _) = CliVpkRekey.BuildPlan(Source(), [armour, HelmetKit()]);

        var head = Assert.Single(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Helmet);
        Assert.Equal(UnitMeshPartLayer.Armor, head.Target.Layer);
        var body = Assert.Single(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Torso);
        Assert.Equal(UnitMeshPartLayer.Undergarment, body.Target.Layer);
    }

    [Fact]
    public void Merge_FallsBackToTheHelmetKitWhenTheTorsoSlotHasASingleLayer()
    {
        // 只有一层就装不下「身体 + 头」两件几何：退回老做法，并且说出来。
        var armour = Target(Part(0x02, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any));
        armour = armour with { ArchiveId = ArmorArchive, Category = "Armor", DisplayName = "一层护甲" };

        var (plan, notes, headMerge) = CliVpkRekey.BuildPlan(Source(), [armour, HelmetKit()]);

        var head = Assert.Single(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Helmet);
        Assert.Equal(UnitMeshPartKind.Helmet, head.Target.PartKind);
        Assert.Contains(notes, note => note.Contains("装不下", StringComparison.Ordinal));
        // 装不下要说成"装不下"，不能只说"没合并"——那两种情况的处置完全不同。
        Assert.Equal(VpkHeadMergeState.UnavailableSingleLayer, headMerge);
    }

    [Fact]
    public void Merge_CanBeTurnedOff()
    {
        var (plan, notes, headMerge) = CliVpkRekey.BuildPlan(Source(), [ThreeLayerArmour(), HelmetKit()], mergeHeadIntoArmour: false);

        var head = Assert.Single(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Helmet);
        Assert.Equal(UnitMeshPartKind.Helmet, head.Target.PartKind);
        var body = Assert.Single(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Torso);
        Assert.Equal(UnitMeshPartLayer.Accessory, body.Target.Layer);
        Assert.DoesNotContain(notes, note => note.Contains("头身合并", StringComparison.Ordinal));
        Assert.Equal(VpkHeadMergeState.Off, headMerge);
    }

    /// <summary>
    /// 目标里没有配套头盔归档（比如目标是别人的自定义素体）：没有独立的头盔 item 可隐藏，
    /// 无从合并。这时必须报"没有头盔套件"，而不是含糊的"没合并"。
    /// </summary>
    [Fact]
    public void Merge_ReportsNoHelmetKitWhenTheTargetHasNoMatchingHelmet()
    {
        var (plan, _, headMerge) = CliVpkRekey.BuildPlan(Source(), [Target(
            Part(0x02, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x03, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any))]);

        Assert.Equal(VpkHeadMergeState.UnavailableNoHelmetKit, headMerge);
        // 头仍然要有落点：没有可合并的地方就照老做法落到 Helmet 件上（这里没有，于是它会报"找不到落点"）。
        Assert.DoesNotContain(plan.Mappings, item => item.Source?.PartKind == UnitMeshPartKind.Helmet);
    }

    private static EquipmentUnitCatalogEntry ThreeLayerArmour()
        => new(ArmorArchive, "Armor", "三层护甲",
        [
            Part(0x12, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x13, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any),
            Part(0x14, UnitMeshPartKind.Torso, UnitMeshPartLayer.Accessory, UnitMeshBodyVariant.Any),
            Part(0x15, UnitMeshPartKind.Hips, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
        ]);

    private static EquipmentUnitCatalogEntry HelmetKit()
        => new(HelmetArchive, "Helmet", "测试头盔",
            [Part(0x21, UnitMeshPartKind.Helmet, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any)]);

    private static EquipmentUnitCatalogEntry Source()
        => new("vpk-mod:test", "CustomModel", "测试素体",
        [
            Part(0x01, UnitMeshPartKind.Helmet, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x02, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x03, UnitMeshPartKind.Hips, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x04, UnitMeshPartKind.LeftLeg, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x05, UnitMeshPartKind.RightLeg, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x06, UnitMeshPartKind.LeftArm, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
            Part(0x07, UnitMeshPartKind.RightArm, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any),
        ]);

    private static EquipmentUnitCatalogEntry Target(params EquipmentUnitPart[] parts)
        => new(ArmorArchive, "Armor", "测试护甲", parts);

    private static EquipmentUnitPart Part(
        ulong fileId, UnitMeshPartKind kind, UnitMeshPartLayer layer, UnitMeshBodyVariant variant)
        => new(
            new AssetKey(0x4d1c334d294dfa97UL, 0x1000UL + fileId),
            MeshInfoIndex: 0,
            MeshId: 0,
            kind,
            layer,
            variant,
            SemanticName: kind.ToString(),
            Confidence: 100,
            SharedArchiveIds: []);
}
