namespace HD2ModCore.Domain;

// Purpose: Distinguishes deployable Mods from decoration packages that must be attached to a host Mod.
public enum ModNodeKind
{
	Standard = 0,
	Decoration = 1,
	// A package option/sub-option. It is stored in the library, but is not a
	// profile member by itself; it is enabled as an attachment to its host.
	Option = 2,
	// A standalone external ReShade component. Its files are deployed to the
	// game's bin directory rather than to GameData.
	ReShade = 3,
}
