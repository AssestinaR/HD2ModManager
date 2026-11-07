using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：判定一个 Mod 目录的存储形态（逻辑 / legacy / sectional / vpk2patch）。
// Purpose: Classifies a Mod directory's on-disk storage shape.
public interface IModStorageKindResolver
{
	ModStorageKind Resolve(string modDirectory);
}
