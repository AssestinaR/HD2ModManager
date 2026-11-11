namespace HD2ModCore.Tests;

/// <summary>
/// 「真映射能不能写盘」这一层共享的串行集合。
///
/// <para>
/// <see cref="HD2ModCore.Domain.ArmorMappingModes.PushAllowWriteForTests"/> 改的是**进程级计数**，
/// 而 xUnit 默认按集合并行。放开写入的用例（编译算法）和断言"写入被拒"的用例
/// 同时跑就会互相踩：前者让后者看到 enabled，后者让前者写不出盘，两边都莫名其妙地红。
/// </para>
/// <para>
/// 所以凡是动这个开关的测试类都必须挂进同一个 collection —— 同 collection 的测试类
/// 不并行，串行跑就各自看得见自己的状态。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ArmorRegistryWriteGateCollection
{
	public const string Name = "ArmorRegistryWriteGate";
}
