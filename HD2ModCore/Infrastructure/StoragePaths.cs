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
	// Pending deletion metadata belongs to the library because it describes physical roots under this library.
	public string PendingDeletionPath => Path.Combine(ModsDirectory, "pending-deletions.json");
	public string DeletionTrashDirectory => Path.Combine(ModsDirectory, ".hd2modmanager-trash");

	/// <summary>
	/// 外部包认领后源目录的去处：<c>&lt;库&gt;.adopted</c>，与库**同级**，不在库里面。
	/// <para>
	/// <b>为什么必须放到库外。</b>认领的源目录本身是「包名/选项/patch」的嵌套形状，
	/// 放进 <see cref="DeletionTrashDirectory"/> 就等于让库根长期背着一棵嵌套树：
	/// 残留清理把它列进排除名单、对账把它当保留目录，而回收站本身从来没有清理入口——
	/// 用户删掉认领出来的节点后，那份副本仍持有数据（硬链接数 2 → 1），空间永远不释放。
	/// 放到同级目录后，<c>mods\</c> 底下彻底没有嵌套目录，也不需要再跟扫描/清理的排除名单
	/// 长期绑定。同卷 ⇒ 仍然是改名，硬链接照旧。
	/// </para>
	/// </summary>
	public string AdoptedArchiveDirectory
	{
		get
		{
			var library = ModsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var parent = Path.GetDirectoryName(library);
			var name = Path.GetFileName(library);
			if (string.IsNullOrEmpty(name)) return Path.Combine(AppRootDirectory, "adopted");
			return Path.Combine(string.IsNullOrEmpty(parent) ? AppRootDirectory : parent, name + ".adopted");
		}
	}
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
