using HD2ModCore.Decoration;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住"生效层由什么组成"。
/// <para>
/// <c>Overwrite/</c> = unit patch ＋ **选中的那一份**材质 patch。没被选中的留在基底目录里、
/// 不进来、也就不部署。这是"两套外观同时存在"的机制本身——放宽成"全部材质都进 Overwrite"，
/// 同一套 unit 上就会有多份材质互相覆盖，用户看到的永远是序号最大的那份。
/// </para>
/// </summary>
public sealed class OverwriteMaterializerTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-overwrite-materializer-tests", Guid.NewGuid().ToString("N"));

	public OverwriteMaterializerTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try
		{
			foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				File.SetAttributes(path, System.IO.FileAttributes.Normal);
			Directory.Delete(_root, recursive: true);
		}
		catch { }
	}

	private string NewMod(string name = "host")
	{
		var root = Path.Combine(_root, name);
		var payload = Path.Combine(root, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(payload);
		return root;
	}

	private string AddPatch(string modRoot, int index, string content = "toc")
	{
		var payload = Path.Combine(modRoot, ModNodePayloadLayout.VpkPatchDirectoryName);
		var path = Path.Combine(payload, $"{Archive}.patch_{index}");
		File.WriteAllText(path, content);
		File.WriteAllText(path + ".gpu_resources", "gpu-" + index);
		return path;
	}

	private static void WriteReport(string modRoot, string unitPatch, string? materialPatch)
	{
		var payload = new System.Text.StringBuilder();
		payload.Append("{\"patches\":[{\"name\":\"host.vpk\",\"kind\":\"model\",\"units\":7,");
		payload.Append("\"materials\":1,\"textures\":2,");
		payload.Append($"\"unitPatch\":\"{unitPatch}\"");
		if (materialPatch is not null) payload.Append($",\"materialPatch\":\"{materialPatch}\"");
		payload.Append("}]}");
		File.WriteAllText(Path.Combine(modRoot, "vpk2patch.json"), payload.ToString());
	}

	private string Overwrite(string modRoot) => Path.Combine(modRoot, ModNodePayloadLayout.OverwriteDirectoryName);

	[Fact]
	public void Materialize_WithNoSelection_WritesOnlyTheUnitLayer()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1);

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		// 没被选中的材质**不能**进 Overwrite —— 它会和 unit 一起部署、互相覆盖。
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.True(ManagedOverwriteDirectory.IsManaged(Overwrite(mod)));
	}

	[Fact]
	public void Materialize_WithSelection_WritesUnitLayerAndThatMaterialOnly()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "material-one");
		AddPatch(mod, 2, "material-two");

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_2"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal("material-two", File.ReadAllText(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
	}

	[Fact]
	public void Materialize_SwitchingSelection_ReplacesThePreviousOne()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "one");
		AddPatch(mod, 2, "two");
		OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_1"]));

		OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_2"]));

		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.Equal("two", File.ReadAllText(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
	}

	[Fact]
	public void Materialize_IsIdempotent()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1);

		var first = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_1"]));
		var second = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_1"]));

		Assert.True(first.Succeeded && second.Succeeded);
		Assert.Equal(first.WrittenFiles.OrderBy(x => x), second.WrittenFiles.OrderBy(x => x));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_UsesTheSuppliedUnitLayerInsteadOfThePayloadOne()
	{
		// 换甲产出的 unit 层要替换掉基底里的那份（unit 已经被搬到目标护甲的键上）。
		var mod = NewMod();
		AddPatch(mod, 0, "baseline-unit");
		var rekeyed = Path.Combine(_root, "rekey-out", $"{Archive}.patch_0");
		Directory.CreateDirectory(Path.GetDirectoryName(rekeyed)!);
		File.WriteAllText(rekeyed, "rekeyed-unit");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default, rekeyed);

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal("rekeyed-unit", File.ReadAllText(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
	}

	// 实测形状（恋星milfy_bill）：宿主**自己那次转换**同时产出了 unitPatch=patch_0 与
	// materialPatch=patch_1。那是同一份来源的**基底材质**，不是变体。把它漏掉，
	// unit 引用的材质 id 就没人定义——游戏里就是黑模。
	[Fact]
	public void Materialize_IncludesBaseMaterialPatchesDeclaredByTheHostsOwnReport()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "base-material");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_EnablingSeveralMaterialOptions_InstallsAllOfThem()
	{
		// 多个材质包同时启用是**正常用法**：它们之间材质 id 可能冲突、后加载的覆盖先加载的，
		// 那是包作者之间的事。管理器只负责诚实部署用户勾了的那几个。
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "built-in");
		AddPatch(mod, 2, "extra-pack");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod,
			new OverwriteSelection([$"{Archive}.patch_1", $"{Archive}.patch_2"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
		Assert.Empty(result.Warnings);
	}

	[Fact]
	public void Materialize_ExplicitSelectionIsExact_NoHiddenDefaults()
	{
		// 用户明确只勾了 patch_2，就只部署 patch_2——不替他补上内置材质。
		// 他要两个都在就勾两个（见上一条用例）。
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "built-in");
		AddPatch(mod, 2, "only-this");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_2"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_WithoutReport_TreatsOnlyTheUnitPatchAsBase()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "would-be-material");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		// 没有报告就说不出它是基底还是变体：**不猜**，只放 unit 层。
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_DefaultReadsTheModsOwnSettings()
	{
		// 用户改过选择之后，默认物化要照它来，而不是永远退回"只有模型内置材质"。
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "built-in");
		AddPatch(mod, 2, "chosen");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");
		ModSettingsFile.TryWrite(mod, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, [$"{Archive}.patch_2"]));

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_WithoutSettings_FallsBackToTheBuiltInMaterial()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "built-in");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	// 换过甲的 Mod：Overwrite 里的 unit patch 是**换甲产物**，与基底里那份不是一回事。
	// 改材质选择时若拿基底那份重建生效层，用户选的护甲就被悄悄换回未换甲的样子。
	// 真库里有 8 个这样的 Mod。
	[Fact]
	public void Materialize_KeepsTheRekeyedUnitPatchThatAlreadyLivesInOverwrite()
	{
		var mod = NewMod();
		AddPatch(mod, 0, "baseline-unit");
		AddPatch(mod, 1, "material");
		var overwrite = Overwrite(mod);
		Directory.CreateDirectory(overwrite);
		File.WriteAllText(Path.Combine(overwrite, $"{Archive}.patch_0"), "rekeyed-unit");
		File.WriteAllText(Path.Combine(overwrite, $"{Archive}.patch_0.gpu_resources"), "rekeyed-gpu");
		ManagedOverwriteDirectory.Commit(overwrite, ManagedOverwriteMarkers.VpkRekey);

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_1"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal("rekeyed-unit", File.ReadAllText(Path.Combine(overwrite, $"{Archive}.patch_0")));
		Assert.Equal("rekeyed-gpu", File.ReadAllText(Path.Combine(overwrite, $"{Archive}.patch_0.gpu_resources")));
		Assert.True(File.Exists(Path.Combine(overwrite, $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_WithoutPayload_Fails()
	{
		var mod = Path.Combine(_root, "bare");
		Directory.CreateDirectory(mod);

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.False(result.Succeeded);
		Assert.Contains("基底目录", result.Error);
	}

	[Fact]
	public void Materialize_WithOneMissingSelection_SkipsItAndKeepsTheRest()
	{
		// 一个材质文件没了，不该让另外几个也白勾——跳过它、点名、照常物化。
		// 否掉整次只会让用户卡住：他面前什么都没有，还被告知"去解决"。
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "one");

		var result = OverwriteMaterializer.Materialize(mod,
			new OverwriteSelection([$"{Archive}.patch_1", $"{Archive}.patch_9"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.Contains(result.Warnings, warning => warning.Contains($"{Archive}.patch_9", StringComparison.Ordinal));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_9")));
	}

	[Fact]
	public void Materialize_RefusesAForeignOverwriteDirectory()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		var overwrite = Overwrite(mod);
		Directory.CreateDirectory(overwrite);
		File.WriteAllText(Path.Combine(overwrite, "user-file.txt"), "hand made");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.Default);

		Assert.False(result.Succeeded);
		Assert.True(File.Exists(Path.Combine(overwrite, "user-file.txt")));
	}

	[Fact]
	public void Materialize_WhenSelectionIsTheUnitLayer_IgnoresIt()
	{
		// unit 层本来就在生效层里；把它当材质勾选没有意义，但也不是错误——
		// 跳过就好，不必弹一条用户看不懂的警告。
		var mod = NewMod();
		AddPatch(mod, 0);

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection([$"{Archive}.patch_0"]));

		Assert.True(result.Succeeded, result.Error);
		Assert.Empty(result.Warnings);
		Assert.Single(Directory.EnumerateFiles(Overwrite(mod), $"*{Archive}.patch_0"));
	}
}
