using System.Text;
using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// VPK 归档：目录树解析、内联数据的偏移换算、preload 拼接，以及"条目步长与版本号无关"。
/// </summary>
public sealed class VpkArchiveTests
{
	private static readonly VpkFileSpec[] SampleFiles =
	[
		new VpkFileSpec("mdl", "models/foo", "bar", [], [1, 2, 3]),
		new VpkFileSpec("vvd", "models/foo", "bar", [], [4, 5]),
		new VpkFileSpec(" ", " ", "root_file", [9], [8, 7]),
	];

	/// <summary>实测的求生之路 2 形状：版本号写 1，但条目是 18 字节。</summary>
	private static byte[] SampleVpk() => BuildVpk(1, 18, SampleFiles);

	private static void WithArchive(byte[] content, Action<VpkArchive> assert)
	{
		var path = WriteTempFile(".vpk", content);
		try
		{
			using var archive = VpkArchive.Open(path);
			assert(archive);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Open_ReadsVersionAndDataOffset()
	{
		WithArchive(SampleVpk(), archive =>
		{
			Assert.Equal(1, archive.Version);
			Assert.True(archive.DataOffset > 12);
			Assert.Equal(3, archive.Entries.Count);
		});
	}

	[Fact]
	public void Open_ParsesAVersion1ArchiveWithEighteenByteEntries()
	{
		// 这是实测形状：头按 v1 的 12 字节算，条目却是 18 字节。
		// 若按版本号把条目当成 16 字节，这里会立刻读歪。
		WithArchive(BuildVpk(1, 18, SampleFiles), archive =>
		{
			Assert.NotNull(archive.Find("models/foo/bar.mdl"));
			Assert.NotNull(archive.Find("root_file"));
		});
	}

	[Fact]
	public void Open_AlsoParsesAVersion1ArchiveWithSixteenByteEntries()
	{
		// 规范里 v1 的条目是 16 字节；实测没见过，但换步长重试要能兜住。
		WithArchive(BuildVpk(1, 16, SampleFiles), archive =>
		{
			Assert.NotNull(archive.Find("models/foo/bar.mdl"));
			Assert.NotNull(archive.Find("root_file"));
		});
	}

	[Fact]
	public void Open_ParsesAVersion2Archive()
	{
		WithArchive(BuildVpk(2, 18, SampleFiles), archive =>
		{
			Assert.Equal(2, archive.Version);
			Assert.NotNull(archive.Find("models/foo/bar.mdl"));
		});
	}

	[Fact]
	public void Find_BuildsPathsFromExtensionAndDirectory()
	{
		WithArchive(SampleVpk(), archive =>
		{
			var mdl = archive.Find("models/foo/bar.mdl");
			Assert.NotNull(mdl);
			Assert.Equal("models/foo/bar.mdl", mdl!.Path);
			Assert.Equal("mdl", mdl.Extension);
			Assert.Equal("models/foo", mdl.Directory);
			Assert.Equal("bar", mdl.FileName);
			Assert.True(mdl.IsInline);
			Assert.Equal(3, mdl.TotalLength);
		});
	}

	[Fact]
	public void Find_TreatsASpaceExtensionAsNoExtensionAndASpaceDirectoryAsRoot()
	{
		WithArchive(SampleVpk(), archive =>
		{
			var root = archive.Find("root_file");
			Assert.NotNull(root);
			Assert.Equal("root_file", root!.Path);
			Assert.Single(root.Preload);
			Assert.Equal(3, root.TotalLength);
		});
	}

	[Fact]
	public void Find_IsCaseInsensitiveAndNormalisesBackslashes()
	{
		WithArchive(SampleVpk(), archive =>
		{
			Assert.NotNull(archive.Find(@"MODELS\Foo\BAR.MDL"));
			Assert.NotNull(archive.Find("/models/foo/bar.mdl"));
			Assert.Null(archive.Find("models/foo/missing.mdl"));
		});
	}

	[Fact]
	public void WithExtension_SelectsEntriesByExtension()
	{
		WithArchive(SampleVpk(), archive =>
		{
			Assert.Single(archive.WithExtension("mdl"));
			Assert.Single(archive.WithExtension(".vvd"));
			Assert.Empty(archive.WithExtension("vtx"));
		});
	}

	[Fact]
	public void TryRead_ReturnsTheInlinePayload()
	{
		WithArchive(SampleVpk(), archive =>
		{
			var entry = archive.Find("models/foo/bar.vvd")!;
			Assert.True(archive.TryRead(entry, out var data));
			Assert.Equal(new byte[] { 4, 5 }, data);
		});
	}

	[Fact]
	public void TryRead_JoinsThePreloadWithTheArchivedPayload()
	{
		// preload 内联在目录树里，真实文件是 preload + 归档段，漏掉任何一半都会读到残缺数据。
		WithArchive(SampleVpk(), archive =>
		{
			var entry = archive.Find("root_file")!;
			Assert.Equal(new byte[] { 9 }, entry.Preload);
			Assert.True(archive.TryRead(entry, out var data));
			Assert.Equal(new byte[] { 9, 8, 7 }, data);
		});
	}

	[Fact]
	public void Open_RejectsForeignMagic()
	{
		var vpk = SampleVpk();
		vpk[0] = 0;

		var path = WriteTempFile(".vpk", vpk);
		try
		{
			Assert.Throws<InvalidDataException>(() => VpkArchive.Open(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Open_KeepsTheFirstEntryAndReportsDuplicatePaths()
	{
		// 同一路径出现两次时**不再**拒绝整包，只把数量报出来并保留先出现的那条。
		// 原因：名字解码差异会制造假重复（2026-10-28 样本里 GBK 的 "大腿"/"倒地" 按 UTF-8 解
		// 会变成同一个 U+FFFD 串），旧实现因此把 201 个条目全部正确的包判成"目录树无法解析"。
		var vpk = BuildVpk(1, 18,
		[
			new VpkFileSpec("mdl", "models", "bar", [], [1]),
			new VpkFileSpec("mdl", "models", "bar", [], [2]),
		]);

		var path = WriteTempFile(".vpk", vpk);
		try
		{
			using var archive = VpkArchive.Open(path);
			Assert.Equal(1, archive.DuplicateEntryCount);
			Assert.Single(archive.Entries);

			var entry = archive.Find("models/bar.mdl")!;
			Assert.True(archive.TryRead(entry, out var data));
			Assert.Equal(new byte[] { 1 }, data);
		}
		finally
		{
			File.Delete(path);
		}
	}

	/// <summary>
	/// 目录树里的名字不一定是 UTF-8：中文模组常用 GBK 存 <c>头部</c> / <c>大腿</c> 这类 vgui 图名。
	/// 按 UTF-8 硬解会得到一串 U+FFFD，两个不同的名字因此变成同一个路径。
	/// </summary>
	[Fact]
	public void Open_DecodesGbkEntryNamesInsteadOfCollapsingThemToReplacementCharacters()
	{
		// 中文模组的目录树常用 GBK（CP936）存 vgui 图名。按 UTF-8 硬解会得到一串 U+FFFD，
		// 于是"大腿"和"倒地"变成同一个路径，201 个条目全部正确的包被判成"目录树无法解析"。
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		var gbk = Encoding.GetEncoding(936);

		var vpk = BuildVpkFromRawNames(1, 18,
		[
			new RawVpkFileSpec(gbk.GetBytes("vtf"), gbk.GetBytes("materials/vgui"), gbk.GetBytes("头部"), [], [1]),
			new RawVpkFileSpec(gbk.GetBytes("vtf"), gbk.GetBytes("materials/vgui"), gbk.GetBytes("大腿"), [], [2]),
			new RawVpkFileSpec(gbk.GetBytes("vtf"), gbk.GetBytes("materials/vgui"), gbk.GetBytes("倒地"), [], [3]),
		]);

		var path = WriteTempFile(".vpk", vpk);
		try
		{
			using var archive = VpkArchive.Open(path);
			Assert.Equal(0, archive.DuplicateEntryCount);
			Assert.Equal(3, archive.Entries.Count);
			Assert.NotNull(archive.Find("materials/vgui/头部.vtf"));
			Assert.NotNull(archive.Find("materials/vgui/大腿.vtf"));
			Assert.NotNull(archive.Find("materials/vgui/倒地.vtf"));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
