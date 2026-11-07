using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「基底目录改名不影响内容指纹」。
/// <para>
/// 老库把 <c>vpk2patch/</c> 改名成 <c>sectional/</c> 是内容不变的迁移，但相对路径进过指纹。
/// 真机上踩过一次：`library sync apply` 先跑迁移（改名），再回头验证计划 ——
/// 发现指纹变了，报 <c>LibrarySyncInputChanged</c> 拒绝执行。结果**改名已经发生、
/// 库记录没更新**，用户看到的是"被拒绝了"，其实磁盘已经变了。
/// </para>
/// </summary>
public sealed class ModLibraryFingerprintTests : IDisposable
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-fingerprint-tests", Guid.NewGuid().ToString("N"));

	public ModLibraryFingerprintTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewMod(string name, string layer)
	{
		var directory = Path.Combine(_root, name, layer);
		Directory.CreateDirectory(directory);
		var patch = Path.Combine(directory, PatchName);
		File.WriteAllText(patch, "toc");
		File.SetLastWriteTimeUtc(patch, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		return Path.Combine(_root, name);
	}

	private async Task<string> FingerprintAsync(string modDirectory)
	{
		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());
		var inspection = await synchronizer.InspectAsync(
			new LibrarySnapshot(1, DateTimeOffset.UnixEpoch, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>()),
			_path);
		return inspection.Directories
			.Single(directory => string.Equals(
				directory.RelativePath, Path.GetFileName(modDirectory), StringComparison.Ordinal))
			.ContentFingerprint;
	}

	private string _path = string.Empty;

	[Fact]
	public async Task RenamingTheBaseLayer_DoesNotChangeTheFingerprint()
	{
		_path = _root;
		var legacy = NewMod("mod", ModNodePayloadLayout.LegacyVpkPatchDirectoryName);
		var current = NewMod("mod2", ModNodePayloadLayout.VpkPatchDirectoryName);

		Assert.Equal(await FingerprintAsync(current), await FingerprintAsync(legacy));
	}
}
