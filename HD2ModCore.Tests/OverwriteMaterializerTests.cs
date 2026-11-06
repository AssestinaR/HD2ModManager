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

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None);

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

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_2"));

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
		OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_1"));

		OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_2"));

		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.Equal("two", File.ReadAllText(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
	}

	[Fact]
	public void Materialize_IsIdempotent()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1);

		var first = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_1"));
		var second = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_1"));

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

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None, rekeyed);

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

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_VariantWithoutOwnMaterial_StillWorks()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "base-material");
		AddPatch(mod, 2, "variant");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_2"));

		Assert.True(result.Succeeded, result.Error);
		// 基底材质 + 选中的变体都在；另一份变体（这里没有）不在。
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_2")));
	}

	[Fact]
	public void Materialize_SelectingABaseMaterial_WarnsInsteadOfInstallingTwice()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "base-material");
		WriteReport(mod, unitPatch: $"{Archive}.patch_0", materialPatch: $"{Archive}.patch_1");

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_1"));

		Assert.True(result.Succeeded, result.Error);
		Assert.Contains(result.Warnings, warning => warning.Contains("基底材质", StringComparison.Ordinal));
	}

	[Fact]
	public void Materialize_WithoutReport_TreatsOnlyTheUnitPatchAsBase()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "would-be-material");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None);

		Assert.True(result.Succeeded, result.Error);
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_0")));
		// 没有报告就说不出它是基底还是变体：**不猜**，只放 unit 层。
		Assert.False(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_WithoutPayload_Fails()
	{
		var mod = Path.Combine(_root, "bare");
		Directory.CreateDirectory(mod);

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None);

		Assert.False(result.Succeeded);
		Assert.Contains("基底目录", result.Error);
	}

	[Fact]
	public void Materialize_WithMissingSelection_FailsAndLeavesPreviousOverwriteIntact()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		AddPatch(mod, 1, "one");
		OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_1"));

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_9"));

		Assert.False(result.Succeeded);
		// 失败不该把用户当前看到的外观抹掉。
		Assert.True(File.Exists(Path.Combine(Overwrite(mod), $"{Archive}.patch_1")));
	}

	[Fact]
	public void Materialize_RefusesAForeignOverwriteDirectory()
	{
		var mod = NewMod();
		AddPatch(mod, 0);
		var overwrite = Overwrite(mod);
		Directory.CreateDirectory(overwrite);
		File.WriteAllText(Path.Combine(overwrite, "user-file.txt"), "hand made");

		var result = OverwriteMaterializer.Materialize(mod, OverwriteSelection.None);

		Assert.False(result.Succeeded);
		Assert.True(File.Exists(Path.Combine(overwrite, "user-file.txt")));
	}

	[Fact]
	public void Materialize_WhenSelectionIsTheUnitLayer_TreatsItAsNoSelection()
	{
		var mod = NewMod();
		AddPatch(mod, 0);

		var result = OverwriteMaterializer.Materialize(mod, new OverwriteSelection($"{Archive}.patch_0"));

		Assert.True(result.Succeeded, result.Error);
		Assert.Single(result.Warnings);
	}
}
