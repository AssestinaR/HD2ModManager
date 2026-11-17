using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 重转/换甲需要的外部事实：游戏 data 目录与存储路径。
/// <para>
/// 做成显式参数而不是让服务自己读全局设置，是为了让命令行也能用同一套逻辑——
/// 命令行没有 GUI 的设置对象，但它知道这两个值。
/// </para>
/// </summary>
/// <param name="GameDataFolder">游戏 data 目录；转换要读外壳 Unit 与 avatar 骨架。</param>
/// <param name="Paths">管理器根与 Mod 库根。</param>
public sealed record VpkHostContext(string? GameDataFolder, StoragePaths Paths)
{
	/// <summary>
	/// 设置页的全局开关「低污染模式」。为真时，编译到自身素体的 VPK 模型 Mod 会被压缩成 2/3 个 unit
	/// （规划 <c>kit-instance-mapping-deploy-plan-20261114.md</c> §10）。默认关：关着时编译产物与以前逐字节一致。
	/// </summary>
	public bool LowPollutionMode { get; init; }

	/// <summary>
	/// 这次编译要为哪些飞船 NPC（<see cref="HD2ModCore.Infrastructure.KitInstance.KitNpcRecords"/> 的伪记录 ID）生成专用 unit：
	/// 只有映射到<b>这个素体</b>的 NPC 才生成；空 = 一个都不生成（编译产物里没有 NPC 资源，没映射就不占地方）。
	/// 只对低污染 + 头身合并的素体有意义。
	/// </summary>
	public IReadOnlyList<uint> NpcItemIds { get; init; } = [];

	/// <summary>Mod 的展示名（可选）；缺省时由目录或 settings.json 推导。</summary>
	public string? ModDisplayName { get; init; }

	/// <summary>
	/// 合并几何超过游戏 D3D12 单流 128 MiB 上限时的交互处置回调（GUI 挂载弹窗，CLI 留空安全兜底）。
	/// </summary>
	public SectionalMergeOverflowHandler? OnMergeOverflow { get; init; }

	/// <summary>仅供内部测试替换几何合并器，生产代码恒为 <c>null</c>。</summary>
	internal Func<string, SectionalUnitMergeSpec, CancellationToken, Task<SectionalMergedUnit>>? MergeUnitOverride { get; init; }
}
