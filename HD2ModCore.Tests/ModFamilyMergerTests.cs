using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 家族归拢：把「材质包是独立 Mod」的旧形状并回模型条目。
/// <para>
/// <b>只做两步，刻意不做第三步。</b>搬来源 VPK → 对模型跑一次重构建。产物形状由重构建保证
/// （一次转换、每个输入一个 patch 组、平铺进 <c>sectional/</c>）。以前的迁移器试过
/// "搬 patch + 重写结构文件"，结果产生第二层错误、还得再写一个迁移器去修它。
/// </para>
/// </summary>
public sealed class ModFamilyMergerTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-family-merger-tests", Guid.NewGuid().ToString("N"));

	public ModFamilyMergerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void PlanFindsTheModelAndItsMaterialPacksInTheSameNamespace()
	{
		CreateModel("Elise_Bill", "vpk-elise");
		CreateMaterial("Elise基础材质A1主体", "vpk-elise");
		CreateMaterial("Elise基础材质B1光泽感肌肤", "vpk-elise");
		// 自洽条目（模型自带材质）：同命名空间里没有别的成员，不该进计划。
		CreateModel("Solo_Bill", "vpk-solo");

		var plan = new ModFamilyMerger(new PatchFileNameParser()).Plan(_root);

		var family = Assert.Single(plan.Families);
		Assert.Equal("Elise_Bill", family.Model.DirectoryName);
		Assert.Equal("vpk-elise", family.IdNamespace);
		Assert.Equal(2, family.Materials.Count);
	}

	[Fact]
	public async Task ApplyMovesTheSourceVpksIntoTheModelAndRebuildsOnce()
	{
		CreateModel("Elise_Bill", "vpk-elise");
		CreateMaterial("A1", "vpk-elise");
		CreateMaterial("B1", "vpk-elise");

		var merger = new ModFamilyMerger(new PatchFileNameParser());
		var plan = merger.Plan(_root);
		var rebuilt = new List<string>();
		ValueTask<string?> Rebuild(string directoryName, string directory, CancellationToken token)
		{
			rebuilt.Add(directoryName);
			Assert.True(File.Exists(Path.Combine(directory, "A1.vpk")));
			Assert.True(File.Exists(Path.Combine(directory, "B1.vpk")));
			return ValueTask.FromResult<string?>(null);
		}

		var outcome = await merger.ApplyAsync(_root, plan, Rebuild, CancellationToken.None);

		Assert.Empty(outcome.Errors);
		Assert.Equal(1, outcome.MergedFamilies);
		Assert.Equal(2, outcome.MovedVpks);
		Assert.Equal(["Elise_Bill"], rebuilt);

		var model = Path.Combine(_root, "Elise_Bill");
		Assert.True(File.Exists(Path.Combine(model, "Elise_Bill.vpk")));
		Assert.True(File.Exists(Path.Combine(model, "A1.vpk")));
		Assert.True(File.Exists(Path.Combine(model, "B1.vpk")));
		// 材质包原来的 VPK 已经搬走：空壳交给库对账 + 残留清理，不在这一步删。
		Assert.False(File.Exists(Path.Combine(_root, "A1", "A1.vpk")));
		Assert.True(Directory.Exists(Path.Combine(_root, "A1")));
	}

	[Fact]
	public async Task ApplyRefusesWhenTheSameVpkNameHasADifferentSize()
	{
		CreateModel("Elise_Bill", "vpk-elise");
		CreateMaterial("A1", "vpk-elise");
		// 模型里已经躺着一份同名但**不同大小**的 VPK：两份都可能是对的，不猜。
		File.WriteAllText(Path.Combine(_root, "Elise_Bill", "A1.vpk"), "different-content");

		var merger = new ModFamilyMerger(new PatchFileNameParser());
		var plan = merger.Plan(_root);
		var rebuilt = 0;
		ValueTask<string?> Rebuild(string directoryName, string directory, CancellationToken token)
		{
			rebuilt++;
			return ValueTask.FromResult<string?>(null);
		}

		var outcome = await merger.ApplyAsync(_root, plan, Rebuild, CancellationToken.None);

		Assert.Equal(0, outcome.MergedFamilies);
		Assert.Equal(0, rebuilt);
		Assert.Contains(outcome.Errors, error => error.Contains("同名但大小不同", StringComparison.Ordinal));
	}

	[Fact]
	public void PlanSkipsANamespaceThatHoldsTwoModels()
	{
		CreateModel("ModelA", "vpk-two");
		CreateModel("ModelB", "vpk-two");
		CreateMaterial("Pack", "vpk-two");

		var plan = new ModFamilyMerger(new PatchFileNameParser()).Plan(_root);

		Assert.Empty(plan.Families);
		Assert.Contains(plan.Notes, note => note.Contains("2 个模型主体", StringComparison.Ordinal));
	}

	private void CreateModel(string name, string idNamespace)
	{
		var directory = Path.Combine(_root, name);
		CreatePayload(directory);
		File.WriteAllText(Path.Combine(directory, name + ".vpk"), name);
		File.WriteAllText(Path.Combine(Path.Combine(directory, "sectional"), "custom-model.json"), "{}");
		WriteReport(directory, idNamespace, $$"""
			{ "name": "{{name}}.vpk", "kind": "model", "unitPatch": "{{Archive}}.patch_0" }
			""");
	}

	private void CreateMaterial(string name, string idNamespace)
	{
		var directory = Path.Combine(_root, name);
		CreatePayload(directory);
		File.WriteAllText(Path.Combine(directory, name + ".vpk"), name);
		WriteReport(directory, idNamespace, $$"""
			{ "name": "{{name}}.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_0" }
			""");
	}

	private static void CreatePayload(string modDirectory)
	{
		var payload = Path.Combine(modDirectory, "sectional");
		Directory.CreateDirectory(payload);
		File.WriteAllText(Path.Combine(payload, Archive + ".patch_0"), "toc");
	}

	private static void WriteReport(string modDirectory, string idNamespace, string patchEntry)
	{
		var json = $$"""
			{
			  "ok": true,
			  "idNamespace": "{{idNamespace}}",
			  "patches": [ {{patchEntry}} ]
			}
			""";
		File.WriteAllText(Path.Combine(modDirectory, VpkConversionReport.FileName), json);
	}
}
