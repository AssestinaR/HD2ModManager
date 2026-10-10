using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：自动查找《绝地潜兵 2》的 data 目录，不依赖任何用户设置。
// Purpose: Auto-detects the Helldivers 2 data directory without relying on user settings.
public interface IGameDataLocator
{
	// 返回 null 表示未找到。命中时 GameDataDirectory 保证真实存在。
	// Returns null when nothing was found. When it does return, GameDataDirectory is guaranteed to exist.
	GameDataLocation? Locate();
}
