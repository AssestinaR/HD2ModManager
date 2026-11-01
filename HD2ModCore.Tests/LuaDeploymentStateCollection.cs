namespace HD2ModCore.Tests;

/// <summary>
/// 这些用例都会改 <c>LuaDeploymentService</c> 上的**静态**设置
/// （映射模式 / 物理模式 / 内置 lua 总开关 —— 产品里它们就是设置页上的那三项，进程级状态）。
/// <para>
/// xunit 默认让不同测试类<b>并行</b>跑，于是"一个类把模式设成外置、另一个类断言独占产物"
/// 这种交错会随机失败。2026-11-01 实测：单跑必过、混跑三次挂一次，
/// 报错指向 <c>LuaDeployment_ExternalLuaEntry_…</c>。
/// </para>
/// <para>
/// 放进同一个 collection 之后它们串行执行，静态状态才成立。这里不改成实例字段是因为
/// 那三项在产品里本来就是全局的（设置页写一次、全进程读），为了测试把它们改成实例状态
/// 反而是把测试的方便塞进产品结构。
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LuaDeploymentStateCollection
{
	public const string Name = "lua-deployment-state";
}
