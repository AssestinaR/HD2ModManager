using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 7-Zip 列表解析：<b>第一条是不是"归档自身"必须由内容判定，不能由位置判定。</b>
/// <para>
/// 报障现场：用户导入一个「ronova_shortskirt.vpk + ronva.jpg」的 zip，管理器报
/// "No importable patch or decoration package was found."——它根本没走 VPK 转换。
/// 原因是 <c>ListAsync</c> 用了 <c>-ba</c>（bare），7-Zip 因此不打印归档自身那一段，
/// 而老实现照完整输出的文档"永远删掉第一条"，删掉的正好是那个 .vpk；
/// 于是"包里有 VPK"的判据为假，整包被交回普通导入。
/// </para>
/// <para>
/// 这组用例钉住两条：bare 输出一条都不许少；带归档自身那一段时仍然要把它去掉。
/// </para>
/// </summary>
public sealed class SevenZipListingTests
{
	private const string Archive = @"C:\Downloads\ronova_shortskirt.zip";

	/// <summary>裸输出：第一条就是真实条目，不许被当成归档自身吃掉。</summary>
	[Fact]
	public void BareListingKeepsFirstRealEntry()
	{
		var output = string.Join('\n',
			"Path = ronova_shortskirt.vpk",
			"Folder = -",
			"Size = 72042662",
			"",
			"Path = ronva.jpg",
			"Folder = -",
			"Size = 465126",
			"");

		var entries = SevenZipArchiveExtractor.ParseEntries(output, Archive);

		Assert.Equal(new[] { "ronova_shortskirt.vpk", "ronva.jpg" }, entries);
		// 这条才是当初的判据：包里有 .vpk ⇒ 必须走 VPK 转换。
		Assert.Contains(entries, entry => entry.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>完整输出（不带 -ba）里第一条是归档自己，它必须被去掉。</summary>
	[Fact]
	public void HeaderListingDropsTheArchiveItself()
	{
		var output = string.Join('\n',
			"Path = " + Archive,
			"Type = zip",
			"Physical Size = 18059905",
			"",
			"Path = ronova_shortskirt.vpk",
			"Folder = -",
			"",
			"Path = ronva.jpg",
			"");

		var entries = SevenZipArchiveExtractor.ParseEntries(output, Archive);

		Assert.Equal(new[] { "ronova_shortskirt.vpk", "ronva.jpg" }, entries);
	}

	/// <summary>归档自身以相对名出现（与归档同目录列出时）也不算"自己"——认不出就不删。</summary>
	[Fact]
	public void RelativeEntryIsNeverTreatedAsArchiveItself()
	{
		var output = string.Join('\n',
			"Path = ronova_shortskirt.zip",
			"Type = zip",
			"",
			"Path = ronova_shortskirt.vpk",
			"");

		var entries = SevenZipArchiveExtractor.ParseEntries(output, Archive);

		Assert.Equal(new[] { "ronova_shortskirt.zip", "ronova_shortskirt.vpk" }, entries);
	}

	/// <summary>
	/// 第一个条目与归档**同名不同物**（包里真的有一个同名文件）时必须保留：
	/// 它不是"归档自身"，删了就是静默丢一条。
	/// </summary>
	[Fact]
	public void SameNameFileInArchiveSurvives()
	{
		var output = string.Join('\n',
			"Path = ronova_shortskirt.zip",
			"Folder = -",
			"Size = 1024",
			"",
			"Path = ronova_shortskirt.vpk",
			"");

		var entries = SevenZipArchiveExtractor.ParseEntries(output, Archive);

		Assert.Equal(new[] { "ronova_shortskirt.zip", "ronova_shortskirt.vpk" }, entries);
	}

	[Fact]
	public void EmptyOutputYieldsNoEntries()
	{
		Assert.Empty(SevenZipArchiveExtractor.ParseEntries(string.Empty, Archive));
	}

	/// <summary>只认 <c>Path = </c>，别的字段（Size / Modified / Folder）一律不进结果。</summary>
	[Fact]
	public void OnlyPathLinesAreCollected()
	{
		var output = string.Join('\n',
			"Path = a.vpk",
			"Folder = -",
			"Size = 1",
			"Modified = 2026-11-04 11:35:31",
			"",
			"Path = b/c.vpk",
			"",
			"Path = ",
			"");

		var entries = SevenZipArchiveExtractor.ParseEntries(output, Archive);

		Assert.Equal(new[] { "a.vpk", "b/c.vpk" }, entries);
	}
}
