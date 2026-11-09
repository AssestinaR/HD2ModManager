using System.Text;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 一次 <c>vpk2patch</c> 的产出是「每个输入 VPK 一个子目录」，而存储约定要求
/// <c>sectional/</c> 里**全部 patch 组平铺按序号**。<see cref="VpkPayloadCollector"/>
/// 就是这两者之间唯一的一步。
/// <para>
/// <b>为什么这条链必须钉死。</b>少了它只有两种结果，都很糟：只取第一个子目录 →
/// 模型 patch 进来、材质全丢（"导入成功、游戏里材质没了"）；把整个输出根搬进去 →
/// 得到一堆子目录，而下游所有枚举器只扫顶层（"部署成功、什么都没变"）。
/// 2026-11-08 用户实测就是这两种现象的混合。
/// </para>
/// </summary>
public sealed class VpkPayloadCollectorTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-payload-collector-tests", Guid.NewGuid().ToString("N"));

	public VpkPayloadCollectorTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void CollectFlattensEveryProducedDirectoryIntoSequentialIndexes()
	{
		var convert = Path.Combine(_root, "convert");
		// 三个输入 VPK → 三个子目录，**每个子目录里都有自己的 patch_0**（CLI 的真实形状）。
		WritePatch(Path.Combine(convert, "elise_bill"), Archive + ".patch_0", "unit");
		WritePatch(Path.Combine(convert, "a1"), Archive + ".patch_0", "material-a1");
		WritePatch(Path.Combine(convert, "b1"), Archive + ".patch_0", "material-b1");
		File.WriteAllText(Path.Combine(convert, "elise_bill", "custom-model.json"), "{}");
		File.WriteAllText(Path.Combine(convert, "elise_bill", "sections.json"), "{}");

		var report = ReadReport("""
			{
			  "ok": true,
			  "idNamespace": "vpk-test",
			  "patches": [
			    { "name": "elise_bill.vpk", "kind": "model", "unitPatch": "9ba626afa44a3aa3.patch_0" },
			    { "name": "a1.vpk", "kind": "material", "materialPatch": "9ba626afa44a3aa3.patch_0" },
			    { "name": "b1.vpk", "kind": "material", "materialPatch": "9ba626afa44a3aa3.patch_0" }
			  ]
			}
			""");

		var target = Path.Combine(_root, "mod", "sectional");
		var result = VpkPayloadCollector.Collect(convert, target, report, "elise_bill");

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal(1, result.UnitPatches);
		Assert.Equal(2, result.MaterialPatches);

		// 三份 patch_0 被重排成 0 / 1 / 2，且 unit 一定在 0。
		Assert.Equal("unit", ReadMarker(Path.Combine(target, $"{Archive}.patch_0")));
		Assert.Equal("material-a1", ReadMarker(Path.Combine(target, $"{Archive}.patch_1")));
		Assert.Equal("material-b1", ReadMarker(Path.Combine(target, $"{Archive}.patch_2")));

		// 结构文件只从模型目录来一份。
		Assert.True(File.Exists(Path.Combine(target, "custom-model.json")));
		Assert.True(File.Exists(Path.Combine(target, "sections.json")));

		// **不建子目录**：这是存储约定的硬要求，下游所有枚举器都只扫顶层。
		Assert.Empty(Directory.GetDirectories(target));
	}

	[Fact]
	public void CollectKeepsSidecarsWithTheirOwnPatch()
	{
		var convert = Path.Combine(_root, "convert");
		WritePatch(Path.Combine(convert, "model"), Archive + ".patch_0", "unit");
		File.WriteAllText(Path.Combine(convert, "model", Archive + ".patch_0.gpu_resources"), "unit-gpu");
		WritePatch(Path.Combine(convert, "pack"), Archive + ".patch_0", "material");
		File.WriteAllText(Path.Combine(convert, "pack", Archive + ".patch_0.gpu_resources"), "material-gpu");

		var report = ReadReport("""
			{
			  "ok": true,
			  "patches": [
			    { "name": "model.vpk", "kind": "model", "unitPatch": "9ba626afa44a3aa3.patch_0" },
			    { "name": "pack.vpk", "kind": "material", "materialPatch": "9ba626afa44a3aa3.patch_0" }
			  ]
			}
			""");

		var target = Path.Combine(_root, "mod", "sectional");
		var result = VpkPayloadCollector.Collect(convert, target, report, "model");

		Assert.True(result.Succeeded, result.Error);
		// sidecar 必须跟着自己那份 base 走：TOC 里写的是它的偏移与长度，拆开就是"读出来是错的、但不报错"。
		Assert.Equal("unit-gpu", File.ReadAllText(Path.Combine(target, $"{Archive}.patch_0.gpu_resources")));
		Assert.Equal("material-gpu", File.ReadAllText(Path.Combine(target, $"{Archive}.patch_1.gpu_resources")));
	}

	[Fact]
	public void CollectWithoutAReportStillPutsTheModelDirectoryFirst()
	{
		var convert = Path.Combine(_root, "convert");
		// 报告缺失（旧产物）：只能靠 custom-model.json 认模型目录。
		WritePatch(Path.Combine(convert, "aaa_material"), Archive + ".patch_0", "material");
		WritePatch(Path.Combine(convert, "zzz_model"), Archive + ".patch_0", "unit");
		File.WriteAllText(Path.Combine(convert, "zzz_model", "custom-model.json"), "{}");

		var target = Path.Combine(_root, "mod", "sectional");
		var result = VpkPayloadCollector.Collect(convert, target, report: null);

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal("unit", ReadMarker(Path.Combine(target, $"{Archive}.patch_0")));
		Assert.Equal("material", ReadMarker(Path.Combine(target, $"{Archive}.patch_1")));
	}

	/// <summary>
	/// 报告里每个来源 VPK 记的都是**它自己那个子目录里**的文件名（都叫 <c>patch_0</c>）。
	/// 收成平铺之后必须把报告里的名字一起改掉，否则 <c>patches.json</c> 认不出哪一份是材质、
	/// 叫什么名字，界面就退化成「材质 1 / 2 / 3」。
	/// </summary>
	[Fact]
	public void CollectRewritesTheReportToTheFlattenedIndexes()
	{
		var convert = Path.Combine(_root, "convert");
		WritePatch(Path.Combine(convert, "elise_bill"), Archive + ".patch_0", "unit");
		WritePatch(Path.Combine(convert, "a1"), Archive + ".patch_0", "material-a1");
		WritePatch(Path.Combine(convert, "b1"), Archive + ".patch_0", "material-b1");
		File.WriteAllText(Path.Combine(convert, "elise_bill", "custom-model.json"), "{}");

		// 三个条目全都引用 patch_0 —— 这就是 CLI 的真实报告。
		var raw = $$"""
			{
			  "ok": true,
			  "idNamespace": "vpk-test",
			  "patches": [
			    { "name": "Elise_Bill.vpk", "kind": "model", "unitPatch": "{{Archive}}.patch_0" },
			    { "name": "A1.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_0" },
			    { "name": "B1.vpk", "kind": "material", "materialPatch": "{{Archive}}.patch_0" }
			  ]
			}
			""";

		var target = Path.Combine(_root, "mod", "sectional");
		var result = VpkPayloadCollector.Collect(
			convert, target, VpkConversionReport.TryParse(raw), "Elise_Bill", rawReportJson: raw);

		Assert.True(result.Succeeded, result.Error);
		Assert.NotNull(result.ReportJson);

		var report = VpkConversionReport.TryParse(result.ReportJson!);
		Assert.NotNull(report);
		Assert.Equal($"{Archive}.patch_0", report!.Patches[0].UnitPatch);
		Assert.Equal($"{Archive}.patch_1", report.Patches[1].MaterialPatch);
		Assert.Equal($"{Archive}.patch_2", report.Patches[2].MaterialPatch);
	}

	/// <summary>
	/// 物理旁车按**前缀**认，不按写死的名单。
	/// <para>
	/// 这是 2026-11-09 那次"游戏里完全没效果"的现场：收集器的结构文件名单里只有
	/// <c>physbone-rig.bin</c>，后来新增的 <c>physbone-driver-rig.bin</c> 被静默留在了
	/// 转换目录里 —— 容器里永远只有一条记录，而收集器、编译、部署全都没报错。
	/// 所以这里刻意用一个**名单里没有**的旁车名字来钉这条规则。
	/// </para>
	/// </summary>
	[Fact]
	public void CollectTakesEveryPhysBoneAuxFileNotJustTheListedOnes()
	{
		var convert = Path.Combine(_root, "convert");
		WritePatch(Path.Combine(convert, "model"), Archive + ".patch_0", "unit");
		var modelDirectory = Path.Combine(convert, "model");
		File.WriteAllText(Path.Combine(modelDirectory, "custom-model.json"), "{}");
		File.WriteAllBytes(Path.Combine(modelDirectory, "physbone-rig.bin"), [1, 2, 3]);
		File.WriteAllBytes(Path.Combine(modelDirectory, "physbone-driver-rig.bin"), [4, 5, 6]);
		// 不是旁车的东西不许被顺手带进来（patch 已经在 ③ 里搬过一次，再来一次就是垃圾）。
		File.WriteAllText(Path.Combine(modelDirectory, "notes.txt"), "x");
		File.WriteAllBytes(Path.Combine(modelDirectory, "physics.bin"), [7]);

		var target = Path.Combine(_root, "mod", "sectional");
		var result = VpkPayloadCollector.Collect(convert, target, report: null);

		Assert.True(result.Succeeded, result.Error);
		Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "physbone-rig.bin")));
		Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(target, "physbone-driver-rig.bin")));
		Assert.False(File.Exists(Path.Combine(target, "notes.txt")));
		Assert.False(File.Exists(Path.Combine(target, "physics.bin")));
	}

	[Fact]
	public void CollectFailsWhenNothingWasProduced()
	{
		var convert = Path.Combine(_root, "convert");
		Directory.CreateDirectory(convert);

		var result = VpkPayloadCollector.Collect(convert, Path.Combine(_root, "mod", "sectional"), report: null);

		Assert.False(result.Succeeded);
		Assert.Contains("没有产出任何 patch", result.Error, StringComparison.Ordinal);
	}

	private VpkConversionReport? ReadReport(string json)
	{
		var directory = Path.Combine(_root, "report-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, VpkConversionReport.FileName), json);
		return VpkConversionReport.TryRead(directory);
	}

	/// <summary>一份"读得出来"的空 patch TOC（魔数 + 条目数 0），后面跟一段标记文本供断言。</summary>
	private static void WritePatch(string directory, string name, string marker)
	{
		Directory.CreateDirectory(directory);
		var header = new byte[0xc0];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0);
		var body = Encoding.UTF8.GetBytes(marker);
		File.WriteAllBytes(Path.Combine(directory, name), [.. header, .. body]);
	}

	private static string ReadMarker(string path)
		=> Encoding.UTF8.GetString(File.ReadAllBytes(path), 0xc0, (int)new FileInfo(path).Length - 0xc0);
}
