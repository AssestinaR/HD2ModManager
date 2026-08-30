namespace HD2ModCore.Infrastructure;

public sealed record StoragePaths(string AppRootDirectory, string? ModsRootDirectory = null)
{
	// 作用：配置与缓存保留在程序根目录，同时允许 Mod 库使用独立的权威路径。
	// Purpose: Keeps configuration/cache under the app root while allowing an authoritative external Mod library.
	public string DataDirectory => Path.Combine(AppRootDirectory, "data");
	public string LibraryDirectory => ModsDirectory;
	public string ImportTempDirectory => Path.Combine(Directory.GetParent(ModsDirectory)?.FullName ?? AppRootDirectory, "tmp");
	public string ModsDirectory => string.IsNullOrWhiteSpace(ModsRootDirectory) ? Path.Combine(AppRootDirectory, "mods") : Path.GetFullPath(ModsRootDirectory);
	public string LibraryPath => Path.Combine(ModsDirectory, "library.json");
	// Decoration activation is library-local because decoration and host IDs both
	// belong to this library snapshot.
	public string DecorationActivationsPath => Path.Combine(ModsDirectory, "decoration-activations.json");
	// Pending deletion metadata belongs to the library because it describes physical roots under this library.
	public string PendingDeletionPath => Path.Combine(ModsDirectory, "pending-deletions.json");
	public string DeletionTrashDirectory => Path.Combine(ModsDirectory, ".hd2modmanager-trash");
	// Profiles belong to a Mod library because their entries reference that library's node IDs.
	public string ProfilesPath => Path.Combine(ModsDirectory, "profiles.json");
	public string LegacyProfilesPath => Path.Combine(DataDirectory, "profiles.json");
	public string LegacyProfileMigrationMarkerPath => Path.Combine(DataDirectory, "profiles-library-migration-v1.complete");
	public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
	public string IndexDirectory => Path.Combine(DataDirectory, "indexes");
	public string DbPath => Path.Combine(IndexDirectory, "asset-index.sqlite");
	public string ModFactsDbPath => Path.Combine(IndexDirectory, "mod-facts.sqlite");
	public string ResourcesDirectory => Path.Combine(DataDirectory, "resources");
	public string ArchiveHashesPath => Path.Combine(ResourcesDirectory, "archivehashes.json");
	public string BoneHashesPath => Path.Combine(ResourcesDirectory, "bonehash.txt");
	public string TypeHashesPath => Path.Combine(ResourcesDirectory, "typehash.txt");
	public string FriendlyNamesPath => Path.Combine(ResourcesDirectory, "friendlynames.txt");
	public string AssetMetadataManifestPath => Path.Combine(ResourcesDirectory, "asset-metadata-manifest.json");
}
