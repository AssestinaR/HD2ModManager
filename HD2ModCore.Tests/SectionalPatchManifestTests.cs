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
	public async Task 旧键sourceVpkName仍能读成SourceName()
	{
		// 1.5.x 的清单把来源名写成 sourceVpkName。字段泛化成 sourceName 之后，
		// 老库必须照读——否则每一个既有 vpk2patch 模组的材质组都会丢掉来源名，
		// 表现为选项名集体退回默认值。
		var payload = Payload();
		Touch(payload, $"{Archive}.patch_0");
		Touch(payload, $"{Archive}.patch_1");
		File.WriteAllText(Path.Combine(payload, SectionalPatchManifest.FileName), $$"""
			{
			  "schema": "hd2mod.patch-groups/1",
			  "generatedUtc": "2026-11-08T00:00:00+00:00",
			  "patches": [
			    { "patchIndex": 0, "archiveHex16": "{{Archive}}", "role": "unit", "sourceVpkName": "Elise_Bill.vpk" },
			    { "patchIndex": 1, "archiveHex16": "{{Archive}}", "role": "material", "sourceVpkName": "Elise_Bill.vpk", "optionName": "我的发色" }
			  ]
			}
			""");

		var document = await SectionalPatchManifestFile.TryReadAsync(payload);

		Assert.NotNull(document);
		Assert.All(document!.Patches, entry => Assert.Equal("Elise_Bill.vpk", entry.SourceName));
		Assert.Equal("我的发色", document.Patches.Single(entry => entry.PatchIndex == 1).OptionName);
	}

	[Fact]
	public async Task 新键sourceName优先且不被旧键覆盖()
	{
		var payload = Payload();
		Touch(payload, $"{Archive}.patch_0");
		File.WriteAllText(Path.Combine(payload, SectionalPatchManifest.FileName), $$"""
			{
			  "schema": "hd2mod.patch-groups/1",
			  "generatedUtc": "2026-11-09T00:00:00+00:00",
			  "patches": [
			    { "patchIndex": 0, "archiveHex16": "{{Archive}}", "role": "unit", "sourceName": "来源条目A", "sourceVpkName": "老名字.vpk" }
			  ]
			}
			""");

		var document = await SectionalPatchManifestFile.TryReadAsync(payload);

		Assert.NotNull(document);
		Assert.Equal("来源条目A", Assert.Single(document!.Patches).SourceName);
	}

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
