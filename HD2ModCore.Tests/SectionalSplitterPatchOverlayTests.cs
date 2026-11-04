using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 拆件器把"一个 Mod 目录里的全部 patch"当成一个集合：同一 asset key 以**序号较大者**为胜者。
/// <para>
/// 用合成的 patch 直接验证叠加规则：造两个非 unit 条目（不需要真几何），
/// 让 <c>patch_1</c> 覆盖 <c>patch_0</c> 里的同一个 key，然后看胜者来自哪个文件。
/// </para>
/// </summary>
public sealed class SectionalSplitterPatchOverlayTests : IDisposable
{
	private readonly string _directory;

	public SectionalSplitterPatchOverlayTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "hd2-split-overlay-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public void ParsePatchIndex_ReadsTrailingNumber()
	{
		Assert.Equal(0, SectionalSplitter.ParsePatchIndex("9ba626afa44a3aa3.patch_0"));
		Assert.Equal(7, SectionalSplitter.ParsePatchIndex("x.patch_7"));
		// 解析不出来时排到最后，不冒充"最高优先"。
		Assert.Equal(int.MaxValue, SectionalSplitter.ParsePatchIndex("noindex.patch_x"));
	}

	[Fact]
	public void EnumeratePatchFiles_IgnoresSidecarsAndOrdersByIndex()
	{
		WriteFake("9ba626afa44a3aa3.patch_2");
		WriteFake("9ba626afa44a3aa3.patch_0");
		WriteFake("9ba626afa44a3aa3.patch_1");
		WriteFake("9ba626afa44a3aa3.patch_0.stream");
		WriteFake("9ba626afa44a3aa3.patch_0.gpu_resources");

		var patches = SectionalSplitter.EnumeratePatchFiles(_directory);

		Assert.Equal(
			["9ba626afa44a3aa3.patch_0", "9ba626afa44a3aa3.patch_1", "9ba626afa44a3aa3.patch_2"],
			patches.Select(Path.GetFileName));
	}

	[Fact]
	public async Task HigherPatchIndexWins_ForTheSameAssetKey()
	{
		const ulong typeId = 0x1234;
		await WritePatchAsync("9ba626afa44a3aa3.patch_0", [(typeId, 0xAA), (typeId, 0xBB)]);
		// patch_1 只覆盖 0xAA 那一份。
		await WritePatchAsync("9ba626afa44a3aa3.patch_1", [(typeId, 0xAA)]);

		var (entries, consumed, _) = await SectionalSplitter.ReadMergedEntriesAsync(_directory);

		Assert.Equal(2, entries.Count);
		Assert.Equal(2, consumed.Count);
		var winner = Assert.Single(entries, entry => entry.AssetKey.FileId == 0xAA);
		Assert.Equal("9ba626afa44a3aa3.patch_1", Path.GetFileName(winner.SourceFilePath));
		// 未被覆盖的那份仍然来自 patch_0。
		var survivor = Assert.Single(entries, entry => entry.AssetKey.FileId == 0xBB);
		Assert.Equal("9ba626afa44a3aa3.patch_0", Path.GetFileName(survivor.SourceFilePath));
	}

	[Fact]
	public async Task LoserDoesNotAppear_AndEntryOrderFollowsFirstAppearance()
	{
		const ulong typeId = 0x1234;
		await WritePatchAsync("9ba626afa44a3aa3.patch_0", [(typeId, 0xAA), (typeId, 0xBB)]);
		await WritePatchAsync("9ba626afa44a3aa3.patch_1", [(typeId, 0xAA)]);

		var (entries, _, _) = await SectionalSplitter.ReadMergedEntriesAsync(_directory);

		// 只有两条：败者不"留着但被盖住"，它是彻底不进表。
		Assert.Equal([0xAAul, 0xBBul], entries.Select(entry => entry.AssetKey.FileId));
	}

	[Fact]
	public async Task SinglePatchFile_IsTreatedAsASetOfOne()
	{
		const ulong typeId = 0x1234;
		var path = await WritePatchAsync("9ba626afa44a3aa3.patch_0", [(typeId, 0xAA)]);

		var (entries, consumed, _) = await SectionalSplitter.ReadMergedEntriesAsync(path);

		Assert.Single(entries);
		Assert.Equal([path], consumed);
	}

	[Fact]
	public async Task EmptyDirectory_ReportsThatThereIsNoPatch()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await SectionalSplitter.ReadMergedEntriesAsync(_directory));
	}

	private void WriteFake(string name)
		=> File.WriteAllBytes(Path.Combine(_directory, name), [0]);

	private async Task<string> WritePatchAsync(string name, IReadOnlyList<(ulong TypeId, ulong FileId)> keys)
	{
		var session = new CanonicalPatchSession();
		foreach (var (typeId, fileId) in keys)
		{
			session.AddEntry(new CanonicalPatchSessionEntry(
				new AssetKey(typeId, fileId),
				CanonicalPatchEntryOwnership.TargetOutput,
				[1, 2, 3],
				[],
				[]));
		}
		Assert.True(session.Finalize(CanonicalDependencyClosureValidation.Valid).IsValid);
		await new CanonicalPatchWriter().WriteAsync(session, _directory, name, null, overwriteExisting: true);
		return Path.Combine(_directory, name);
	}
}
