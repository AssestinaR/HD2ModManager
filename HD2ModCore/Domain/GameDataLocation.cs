namespace HD2ModCore.Domain;

// 作用：一次自动查找游戏目录的结果：游戏 data 目录、游戏根目录，以及用于诊断的 Steam 库信息。
// Purpose: Result of one auto-detection pass: the game data directory, the game root directory, and the Steam library info used for diagnostics.
public sealed record GameDataLocation(
	string GameDataDirectory,
	string GameDirectory,
	string MatchedSteamLibrary,
	IReadOnlyList<string> ExaminedSteamLibraries);
