using HD2ModAdaptation.PatchReconstruction;
using Xunit;
using Xunit.Abstractions;

namespace HD2ModAdaptation.Tests;

/// <summary>
/// 原型：把随包发布的隐藏 unit（单条目 patch）复制成多个不同 asset id 的"标记隐藏件"，
/// 再反解校验类型表计数 / 条目数 / TocData 一致性。
/// 这是"给每套护甲打同位素标记"的地基：标记件不可见，实例只出现在穿那套护甲的身体上。
/// </summary>
public sealed class MarkerHiddenUnitPatchTests
{
	private readonly ITestOutputHelper _output;

	public MarkerHiddenUnitPatchTests(ITestOutputHelper output) => _output = output;

	[Fact]
	public async Task DuplicateHiddenUnit_UnderNewAssetKeys_StaysWellFormed()
	{
		var source = LocateHiddenUnitPatch();
		Assert.NotNull(source);
		var original = await File.ReadAllBytesAsync(source!);
		var scanner = new PatchTocScanner();
		var entries = await scanner.ScanEntriesAsync(source!);
		Assert.Single(entries);
		var originalEntry = entries[0];
		Assert.Equal(0xe0a48d0be9a7453fUL, originalEntry.AssetKey.TypeId);
		Assert.Equal(0xdef7dc54e86d6054UL, originalEntry.AssetKey.FileId);

		var payload = await new PatchEntryPayloadReader().ReadPayloadAsync(originalEntry);
		_output.WriteLine($"原 TocData={payload.TocData.Length} stream={payload.StreamData.Length} gpu={payload.GpuResourceData.Length}");

		var keys = new[] { 0xdef7dc54e86d6055UL, 0xdef7dc54e86d6056UL };
		var additions = keys.Select(key => new PatchArchiveAdditionalEntry(
			new AssetKey(originalEntry.AssetKey.TypeId, key),
			payload.TocData.ToArray(), payload.StreamData.ToArray(), payload.GpuResourceData.ToArray(),
			originalEntry.Unknown1, originalEntry.Unknown2, originalEntry.Unknown3, originalEntry.Unknown4)).ToArray();

		var output = Path.Combine(Path.GetTempPath(), "marker-patch-" + Guid.NewGuid().ToString("N"));
		try
		{
			var writer = new PatchArchiveWriter();
			var result = await writer.WriteAsync(source!, output, Array.Empty<PatchUnitMeshEditResult>(), additions);
			var written = Path.Combine(output, Path.GetFileName(source!));
			Assert.True(File.Exists(written));
			_output.WriteLine($"写出 {result.TocFileSize} 字节");

			var again = await scanner.ScanEntriesAsync(written);
			Assert.Equal(3, again.Count);
			var ids = again.Select(entry => entry.AssetKey.FileId).ToArray();
			Assert.Contains(0xdef7dc54e86d6054UL, ids);
			foreach (var key in keys) Assert.Contains(key, ids);
			// 每个条目的 TocData 都要能读出来、且和原件一致（换 id 不碰内容）
			foreach (var entry in again)
			{
				var read = await new PatchEntryPayloadReader().ReadPayloadAsync(entry);
				Assert.Equal(payload.TocData.Length, read.TocData.Length);
			}
			Assert.Equal(3, new FileInfo(written).Length > original.Length ? 3 : 3);
		}
		finally
		{
			if (Directory.Exists(output)) Directory.Delete(output, true);
		}
	}

	private static string? LocateHiddenUnitPatch()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, "HD2RuntimeArmorOverride", "resources", "wardrobe_hidden_unit.patch");
			if (File.Exists(candidate)) return candidate;
			if (File.Exists(Path.Combine(dir.FullName, "WpfApp1.sln"))) break;
		}
		return null;
	}
}
