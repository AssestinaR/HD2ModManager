using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

// 作用：头身合并的**落点选择**与**留痕**。
//
// 素体的头在注册表里永远是唯一一件「头盔槽 / 层 0」（见 VpkPatchAssembler 里 BodyRegion.Head
// 那一条）。合并 = 把它挪到胸部的第一个空层，并写下原来的格子，否则事后没人能证明
// "某个胸甲层的那件就是头盔挪过去的"，也没法据此还原。
public sealed class VpkHeadMergePlacementTests
{
    private const uint HelmetSlot = 0;
    private const uint ChestSlot = 2;

    [Fact]
    public void MovesTheHeadToTheFirstFreeChestLayerAndRecordsWhereItCameFrom()
    {
        VpkRegistryPiece[] pieces =
        [
            new(3, HelmetSlot, 0, false),   // 头：body=3、头盔槽、层 0
            new(0, ChestSlot, 0, true),     // 本体：body 0 与 body 1 各一件，都在胸部层 0
            new(1, ChestSlot, 0, true),
        ];

        var merged = VpkPatchAssembler.MoveHeadToFirstFreeTorsoLayer(pieces, headIndex: 0, torsoSlot: ChestSlot);

        var head = merged[0];
        Assert.Equal(ChestSlot, head.Slot);
        Assert.Equal(1u, head.PieceType);                 // 层 0 被本体件占着，第一个空位是 1
        Assert.Equal("slot0/layer0", head.MovedFrom);     // 留痕：原格子
        // 其余件原样不动。
        Assert.Equal(pieces[1], merged[1]);
        Assert.Equal(pieces[2], merged[2]);
    }

    [Fact]
    public void SkipsALayerAlreadyTakenByAChestArmourPiece()
    {
        VpkRegistryPiece[] pieces =
        [
            new(3, HelmetSlot, 0, false),
            new(3, ChestSlot, 0, true),     // 胸部内衣层
            new(3, ChestSlot, 1, false),    // 胸甲层已被占用
        ];

        var merged = VpkPatchAssembler.MoveHeadToFirstFreeTorsoLayer(pieces, headIndex: 0, torsoSlot: ChestSlot);

        Assert.Equal(2u, merged[0].PieceType);            // 于是落到 2
        Assert.Equal(ChestSlot, merged[0].Slot);
    }

    [Fact]
    public void OccupancyOnOtherSlotsDoesNotCount()
    {
        VpkRegistryPiece[] pieces =
        [
            new(3, HelmetSlot, 0, false),
            new(3, 3, 0, false),            // 胯部层 0：与胸部无关
            new(3, 3, 1, false),
            new(3, ChestSlot, 0, true),
        ];

        var merged = VpkPatchAssembler.MoveHeadToFirstFreeTorsoLayer(pieces, headIndex: 0, torsoSlot: ChestSlot);

        Assert.Equal(1u, merged[0].PieceType);
    }

    [Fact]
    public void FailsLoudlyWhenEveryLayerIsTaken()
    {
        VpkRegistryPiece[] pieces =
        [
            new(3, HelmetSlot, 0, false),
            new(3, ChestSlot, 0, true),
            new(3, ChestSlot, 1, false),
            new(3, ChestSlot, 2, false),
        ];

        var error = Assert.Throws<InvalidOperationException>(
            () => VpkPatchAssembler.MoveHeadToFirstFreeTorsoLayer(pieces, headIndex: 0, torsoSlot: ChestSlot));

        // 报错要能直接照着改：说清是哪一层到哪一层被占满了。
        Assert.Contains("胸部槽位", error.Message);
        Assert.Contains("没有空位", error.Message);
    }
}
