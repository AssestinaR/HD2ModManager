using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：钉住缩略图内容指纹的语义（设计文档 §27.3）。
// Purpose: Pins the semantics of the thumbnail content fingerprint (design doc §27.3).
//
// 这些断言看起来"太基础"，但正是它们承重：
// 磁盘缓存与事实缓存都靠这个指纹判断"这份缩略图还算不算数"，
// 一旦它漏掉"长度/修改时间"，同名换图就会命中旧缩略图，用户看到的是"换了图标但卡片没变"，
// 而且不会自愈——这正是阶段 5 要修掉的 bug（§27.1）。
public sealed class ThumbnailFingerprintTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-thumb-fp-" + Guid.NewGuid().ToString("N"));

	public ThumbnailFingerprintTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, true); } catch { }
	}

	private string WriteIcon(byte[] content, DateTime lastWriteUtc)
	{
		var path = Path.Combine(_root, "icon.png");
		File.WriteAllBytes(path, content);
		File.SetLastWriteTimeUtc(path, lastWriteUtc);
		return path;
	}

	[Fact]
	public void MissingSourceYieldsTheMissingSentinel()
	{
		Assert.Equal(ThumbnailFingerprint.Missing, ThumbnailFingerprint.OfSource(null));
		Assert.Equal(ThumbnailFingerprint.Missing, ThumbnailFingerprint.OfSource(string.Empty));
		Assert.Equal(ThumbnailFingerprint.Missing, ThumbnailFingerprint.OfSource(Path.Combine(_root, "icon.png")));
	}

	[Fact]
	public void SameContentYieldsTheSameFingerprint()
	{
		var path = WriteIcon([1, 2, 3], new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		Assert.Equal(ThumbnailFingerprint.OfSource(path), ThumbnailFingerprint.OfSource(path));
		Assert.NotEqual(ThumbnailFingerprint.Missing, ThumbnailFingerprint.OfSource(path));
	}

	// 这是本阶段要修的 bug：同路径换内容必须换指纹。
	[Fact]
	public void ReplacingTheIconAtTheSamePathChangesTheFingerprint()
	{
		var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		var path = WriteIcon([1, 2, 3], stamp);
		var before = ThumbnailFingerprint.OfSource(path);

		// 长度相同、只有字节与时间不同：证明"修改时间"确实进了指纹，而不是只靠长度。
		var replaced = WriteIcon([7, 8, 9], stamp.AddMinutes(1));
		var after = ThumbnailFingerprint.OfSource(replaced);

		Assert.NotEqual(before, after);
	}

	[Fact]
	public void SourceLengthParticipatesInTheFingerprint()
	{
		var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		var path = WriteIcon([1], stamp);
		var before = ThumbnailFingerprint.OfSource(path);

		var longer = WriteIcon([1, 2], stamp);
		Assert.NotEqual(before, ThumbnailFingerprint.OfSource(longer));
	}

	[Fact]
	public void NodeFingerprintIsNamespacedByNodeAndDiffersFromTheSourceFingerprint()
	{
		var path = WriteIcon([1, 2, 3], new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		var nodeA = new ModNodeId(Guid.NewGuid());
		var nodeB = new ModNodeId(Guid.NewGuid());

		// 同一张图、不同节点：事实缓存按节点寻址，所以必须不同。
		Assert.NotEqual(ThumbnailFingerprint.OfNode(nodeA, path), ThumbnailFingerprint.OfNode(nodeB, path));
		// 节点级指纹不是源指纹本身，否则事实缓存键就丢了命名空间。
		Assert.NotEqual(ThumbnailFingerprint.OfNode(nodeA, path), ThumbnailFingerprint.OfSource(path));
		// 同一节点同一张图必须稳定，否则缓存永远不命中。
		Assert.Equal(ThumbnailFingerprint.OfNode(nodeA, path), ThumbnailFingerprint.OfNode(nodeA, path));
	}

	[Fact]
	public void FingerprintIsLowercaseHexSha256()
	{
		var path = WriteIcon([1, 2, 3], new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
		var fingerprint = ThumbnailFingerprint.OfSource(path);

		Assert.Equal(64, fingerprint.Length);
		Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);
		Assert.True(fingerprint.All(character => Uri.IsHexDigit(character)));
	}
}
