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
public sealed record VpkHostContext(string? GameDataFolder, StoragePaths Paths);
