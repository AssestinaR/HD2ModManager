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
}
