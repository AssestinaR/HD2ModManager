using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>patches.json</c> 里材质选项的**名字**。
/// <para>
/// 一整组（模型 + N 个材质包）转进同一个 Mod 之后，材质选项表里有 N+1 行。以前每个材质包是
/// 独立 Mod，靠 Mod 名区分；合并成一个条目之后，只剩这个字段能区分。全都叫「模型内置材质」
/// 的话，用户在界面上根本不知道哪一行是哪个包。
/// </para>
/// </summary>
public sealed class SectionalPatchOptionNameTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-option-name-tests", Guid.NewGuid().ToString("N"));

	public SectionalPatchOptionNameTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void MaterialFromAnotherVpkIsNamedAfterThatVpk()
	{
		var payload = CreatePayload();
		var report = ReadReport("""
			{
			  "ok": true,
			  "patches": [
			    { "name": "elise_bill.vpk", "kind": "model", "unitPatch": "9ba626afa44a3aa3.patch_0" },
			    { "name": "Elise基础材质A1主体.vpk", "kind": "material", "materialPatch": "9ba626afa44a3aa3.patch_1" }
			  ]
			}
			""");

		var document = SectionalPatchManifestFile.Build(payload, Archive, report, DateTimeOffset.UtcNow);

		var unit = document.Patches.Single(entry => entry.PatchIndex == 0);
		var material = document.Patches.Single(entry => entry.PatchIndex == 1);
		Assert.Equal(SectionalPatchRole.Unit, unit.Role);
		Assert.Equal(SectionalPatchRole.Material, material.Role);
		Assert.Equal("Elise基础材质A1主体", material.OptionName);
	}

	[Fact]
	public void MaterialProducedByTheModelItselfIsCalledBuiltIn()
	{
		var payload = CreatePayload();
		// 模型自己那次转换同时产出了 unit + 材质（例如 恋星milfy_bill）：那份材质叫「模型内置材质」。
		var report = ReadReport("""
			{
			  "ok": true,
			  "patches": [
			    {
			      "name": "milfy_bill.vpk",
			      "kind": "model",
			      "unitPatch": "9ba626afa44a3aa3.patch_0",
			      "materialPatch": "9ba626afa44a3aa3.patch_1"
			    }
			  ]
			}
			""");

		var document = SectionalPatchManifestFile.Build(payload, Archive, report, DateTimeOffset.UtcNow);

		var material = document.Patches.Single(entry => entry.PatchIndex == 1);
		Assert.Equal(SectionalPatchManifest.BuiltInMaterialOptionName, material.OptionName);
	}

	/// <summary>
	/// <c>Rebuild</c> 必须用**当前**报告：换了载荷、报告也换好之后，材质选项要重新拿到名字。
	/// <para>
	/// 这条钉住 2026-11-08 的现场：载荷先换、清单却用**旧**报告写了一遍 —— 新合并进来的
	/// 材质 patch 在旧报告里根本没有，于是 <c>optionName</c> 全是空的，
	/// 界面退化成「材质 1 / 2 / 3」。修法就是把写清单这一步挪到"报告换好之后"。
	/// </para>
	/// </summary>
	[Fact]
	public void RebuildPicksTheNamesFromTheNewReport()
	{
		var modDirectory = Path.Combine(_root, "mod");
		var payload = Path.Combine(modDirectory, "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_0"), "unit");
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_1"), "material-a1");
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_2"), "material-b1");

		// 旧清单：上一批只有模型，所以材质那两行没有名字。
		var stale = SectionalPatchManifestFile.Build(payload, Archive, report: null, DateTimeOffset.UtcNow);
		SectionalPatchManifestFile.TryWrite(payload, stale);
		Assert.Null(stale.Patches.Single(entry => entry.PatchIndex == 1).OptionName);

		// 新报告：模型 + 两个材质包。
		File.WriteAllText(Path.Combine(modDirectory, VpkConversionReport.FileName), $$"""
			{
			  "ok": true,
			  "patches": [
			    { "name": "elise_bill.vpk", "kind": "model", "unitPatch": "{{Archive}}.patch_0" },
			    { "name": "A1.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_1" },
			    { "name": "B1.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_2" }
			  ]
			}
			""");

		var rebuilt = SectionalPatchManifestFile.Rebuild(modDirectory, DateTimeOffset.UtcNow);

		Assert.NotNull(rebuilt);
		Assert.Equal("A1", rebuilt!.Patches.Single(entry => entry.PatchIndex == 1).OptionName);
		Assert.Equal("B1", rebuilt.Patches.Single(entry => entry.PatchIndex == 2).OptionName);
	}

	/// <summary>
	/// 重转之后，用户改过的**选项名/顺序/备注**必须按"来源 VPK"认领回来。
	/// <para>
	/// 不能按序号认领：序号会随报告顺序变，一旦变了，用户给 A 起的名字会接到 B 身上 ——
	/// 那既污染名字，也污染部署顺序，而用户完全看不出是重建干的。
	/// </para>
	/// </summary>
	[Fact]
	public void RebuildCarriesUserFieldsBySourceVpkNotByIndex()
	{
		var modDirectory = Path.Combine(_root, "mod");
		var payload = Path.Combine(modDirectory, "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_0"), "unit");
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_1"), "material");

		// 上一版：用户把这份材质命名为「我的发色」、排序 5、写了备注。
		SectionalPatchManifestFile.TryWrite(payload, new SectionalPatchManifestDocument(
			SectionalPatchManifest.CurrentSchema, DateTimeOffset.UtcNow,
			[
				new SectionalPatchGroupEntry(0, Archive, SectionalPatchRole.Unit, "model.vpk"),
				new SectionalPatchGroupEntry(1, Archive, SectionalPatchRole.Material, "pack.vpk",
					OptionName: "我的发色", Order: 5, Notes: "我改过的备注"),
			]));

		File.WriteAllText(Path.Combine(modDirectory, VpkConversionReport.FileName), $$"""
			{
			  "ok": true,
			  "patches": [
			    { "name": "model.vpk", "kind": "model", "unitPatch": "{{Archive}}.patch_0" },
			    { "name": "pack.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_1" }
			  ]
			}
			""");

		var rebuilt = SectionalPatchManifestFile.Rebuild(modDirectory, DateTimeOffset.UtcNow);

		Assert.NotNull(rebuilt);
		var material = rebuilt!.Patches.Single(entry => entry.Role == SectionalPatchRole.Material);
		Assert.Equal("我的发色", material.OptionName);
		Assert.Equal(5, material.Order);
		Assert.Equal("我改过的备注", material.Notes);
	}

	/// <summary>
	/// 与真实重构建完全同形的一次：报告里**只有一个 model 条目**（它同时产出 unit 与材质），
	/// 上一版清单是**磁盘上的 JSON**（不是内存里构造的）。
	/// </summary>
	[Fact]
	public async Task RebuildCarriesFromDiskManifestWhenTheModelProducesBothPatches()
	{
		var modDirectory = Path.Combine(_root, "mod-disk");
		var payload = Path.Combine(modDirectory, "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_0"), "unit");
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_1"), "material");
		File.WriteAllText(Path.Combine(payload, SectionalPatchManifest.FileName), $$"""
			{
			  "schema": "hd2mod.patch-groups/1",
			  "generatedUtc": "2026-11-08T02:05:29.5972495+00:00",
			  "patches": [
			    { "patchIndex": 0, "archiveHex16": "{{Archive}}", "role": "unit", "sourceVpkName": "Lunalice_Bill.vpk" },
			    { "patchIndex": 1, "archiveHex16": "{{Archive}}", "role": "material", "sourceVpkName": "Lunalice_Bill.vpk", "optionName": "我的发色", "order": 5, "notes": "我改过的备注" }
			  ]
			}
			""");
		File.WriteAllText(Path.Combine(modDirectory, VpkConversionReport.FileName), $$"""
			{
			  "ok": true,
			  "patches": [
			    { "name": "Lunalice_Bill.vpk", "kind": "model", "unitPatch": "{{Archive}}.patch_0", "materialPatch": "{{Archive}}.patch_1" }
			  ]
			}
			""");

		var previous = await SectionalPatchManifestFile.TryReadAsync(payload);
		Assert.NotNull(previous);
		var rebuilt = SectionalPatchManifestFile.Rebuild(modDirectory, DateTimeOffset.UtcNow, previous);

		Assert.NotNull(rebuilt);
		var material = rebuilt!.Patches.Single(entry => entry.Role == SectionalPatchRole.Material);
		Assert.Equal("我的发色", material.OptionName);
		Assert.Equal(5, material.Order);
		Assert.Equal("我改过的备注", material.Notes);
	}

	private string CreatePayload()
	{
		var payload = Path.Combine(_root, "mod", "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_0"), "unit");
		File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_1"), "material");
		return payload;
	}

	private VpkConversionReport? ReadReport(string json)
	{
		var directory = Path.Combine(_root, "report-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, VpkConversionReport.FileName), json);
		return VpkConversionReport.TryRead(directory);
	}
}
