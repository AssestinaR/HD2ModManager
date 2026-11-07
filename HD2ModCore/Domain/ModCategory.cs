namespace HD2ModCore.Domain;

// Purpose: User-facing mod categories derived from scanned patch contents. A mod can belong to several.
[Flags]
public enum ModCategory
{
	None = 0,
	Armor = 1 << 0,
	Cape = 1 << 2,
	Weapon = 1 << 3,
	Audio = 1 << 4,
	Visual = 1 << 5,
	Ui = 1 << 6,
	Animation = 1 << 7,
	Other = 1 << 8,
}
