namespace HD2ModCore.Domain;

// 作用：标识外部安装事实的已知文件类型。
// Purpose: Identifies a known external install file type.
public enum ExternalInstallFileKind
{
	// A native ReShade add-on loaded from the game's bin directory.
	ReShadePlugin,
	// A ReShade preset. Presets are INI files with a root-level Techniques key;
	// they are not executable add-ons, but remain part of the ReShade component
	// family for library recognition and safe external deployment.
	ReShadePreset,
}
