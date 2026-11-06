using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住基底目录里的 <c>patches.json</c>：它要能说清"每个 patch 组是谁"，
/// 且**读不到/坏了必须回退**——它只是便利信息，不是权威。
/// </summary>
public sealed class SectionalPatchManifestTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-patch-manifest-tests", Guid.NewGuid().ToString("N"));

	public SectionalPatchManifestTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string Payload()
	{
		var dir = Path.Combine(_root, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static void Touch(string dir, string name, string content = "toc")
		=> File.WriteAllText(Path.Combine(dir, name), content);

	[Fact]
	public void Build_WithoutReport_FallsBackToTheIndexConvention()
	{
		var payload = Payload();
		Touch(payload, $"{Archive}.patch_0");
		Touch(payload, $"{Archive}.patch_1");
		Touch(payload, $"{Archive}.patch_2");
		// sidecar 不该被当成 patch
		Touch(payload, $"{Archive}.patch_0.gpu_resources");

		var document = SectionalPatchManifestFile.Build(payload, Archive, report: null, DateTimeOffset.UtcNow);

		Assert.Equal(SectionalPatchManifest.CurrentSchema, document.Schema);
		Assert.Equal(3, document.Patches.Count);
		Assert.Equal(SectionalPatchRole.Unit, document.Patches[0].Role);
		Assert.Equal(0, document.Patches[0].PatchIndex);
		Assert.All(document.Patches.Skip(1), entry => Assert.Equal(SectionalPatchRole.Material, entry.Role));
	}

	[Fact]
	public void Build_OrdersByPatchIndex()
	{
		var payload = Payload();
		Touch(payload, $"{Archive}.patch_2");
		Touch(payload, $"{Archive}.patch_0");
		Touch(payload, $"{Archive}.patch_1");

		var document = SectionalPatchManifestFile.Build(payload, Archive, null, DateTimeOffset.UtcNow);

		Assert.Equal([0, 1, 2], document.Patches.Select(entry => entry.PatchIndex).ToArray());
	}

	[Fact]
	public void Build_OnMissingPayload_IsEmpty()
	{
		var document = SectionalPatchManifestFile.Build(
			Path.Combine(_root, "nope"), Archive, null, DateTimeOffset.UtcNow);

		Assert.Empty(document.Patches);
	}

	[Fact]
	public async Task WriteThenRead_RoundTrips()
	{
		var payload = Payload();
		Touch(payload, $"{Archive}.patch_0");
		Touch(payload, $"{Archive}.patch_1");
		var document = SectionalPatchManifestFile.Build(payload, Archive, null, DateTimeOffset.UtcNow);

		Assert.Null(SectionalPatchManifestFile.TryWrite(payload, document));
		var read = await SectionalPatchManifestFile.TryReadAsync(payload);

		Assert.NotNull(read);
		Assert.Equal(2, read!.Patches.Count);
		Assert.Equal(SectionalPatchRole.Unit, read.Patches[0].Role);
		Assert.Equal(SectionalPatchRole.Material, read.Patches[1].Role);
	}

	[Fact]
	public async Task Read_OnBrokenJson_ReturnsNull_InsteadOfThrowing()
	{
		var payload = Payload();
		Touch(payload, SectionalPatchManifest.FileName, "{ this is not json");

		Assert.Null(await SectionalPatchManifestFile.TryReadAsync(payload));
	}

	[Fact]
	public async Task Read_OnUnknownSchema_ReturnsNull()
	{
		var payload = Payload();
		Touch(payload, SectionalPatchManifest.FileName,
			"""{"schema":"hd2mod.patch-groups/999","generatedUtc":"2026-01-01T00:00:00+00:00","patches":[]}""");

		Assert.Null(await SectionalPatchManifestFile.TryReadAsync(payload));
	}

	[Fact]
	public async Task Read_OnMissingFile_ReturnsNull()
	{
		Assert.Null(await SectionalPatchManifestFile.TryReadAsync(Payload()));
	}
}
