using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>vpk2patch.json</c> 的可读投影。
/// <para>
/// 这份投影是「重构建」的唯一依据：命名空间决定材质/贴图 id，转换参数决定产物内容。
/// 读错一样，重构建出来的东西就和原来不是一套——而那种偏差在文件层面看不出来，
/// 只会在游戏里表现为"材质接不上了"或"观感全变了"。所以这里钉三件事：
/// </para>
/// <list type="bullet">
/// <item>该读到的三样（命名空间 / 参数 / 批次队列）都读得到；</item>
/// <item>旧产物（没有 <c>conversion</c> 块）要能被认出来是"没记参数"，而不是当默认值谎报；</item>
/// <item>读不懂一律返回 <c>null</c>，绝不返回半份数据。</item>
/// </list>
/// </summary>
public sealed class VpkConversionReportTests : IDisposable
{
	private readonly string directory = Path.Combine(
		Path.GetTempPath(), "hd2-vpk-report-" + Guid.NewGuid().ToString("N"));

	public VpkConversionReportTests() => Directory.CreateDirectory(directory);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	private string Write(string json)
	{
		var path = Path.Combine(directory, VpkConversionReport.FileName);
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void ReadsNamespaceSettingsAndQueue()
	{
		Write("""
			{
			  "ok": true,
			  "queue": ["C:/a/serenade.vpk", "C:/a/serenade_b1.vpk"],
			  "idNamespace": "vpk-060d479e4bc677d0",
			  "conversion": {
			    "components": "merge",
			    "matcap": "metal",
			    "matcapGain": 1.2,
			    "cutout": "alpha",
			    "albedoFactor": null
			  },
			  "notes": ["这次转换没有命名空间…"],
			  "patches": []
			}
			""");

		var report = VpkConversionReport.TryRead(directory);

		Assert.NotNull(report);
		Assert.Equal("vpk-060d479e4bc677d0", report!.IdNamespace);
		Assert.True(report.HasSettings);
		Assert.Equal("merge", report.Settings.Components);
		Assert.Equal("metal", report.Settings.Matcap);
		Assert.Equal(1.2, report.Settings.MatcapGain);
		Assert.Equal("alpha", report.Settings.Cutout);
		Assert.Null(report.Settings.AlbedoFactor);
		Assert.Equal(2, report.Queue.Count);
		Assert.Single(report.Notes);
	}

	/// <summary>没有 <c>conversion</c> 块 = 旧产物。必须能被认出来，否则重构建会以为"当时就是这么转的"。</summary>
	[Fact]
	public void OldProductWithoutConversionBlockReportsNoSettings()
	{
		Write("""{ "ok": true, "idNamespace": "vpk-0123456789abcdef", "patches": [] }""");

		var report = VpkConversionReport.TryRead(directory);

		Assert.NotNull(report);
		Assert.False(report!.HasSettings);
		Assert.Equal(VpkConversionSettings.Default, report.Settings);
		// 命名空间仍然要读得到：它决定 id，缺了它重构建就一定对不上。
		Assert.Equal("vpk-0123456789abcdef", report.IdNamespace);
	}

	[Fact]
	public void MissingOrUnreadableFileYieldsNull()
	{
		Assert.Null(VpkConversionReport.TryRead(directory));

		Write("{ 这不是 JSON");
		Assert.Null(VpkConversionReport.TryRead(directory));

		Write("[1, 2, 3]");
		Assert.Null(VpkConversionReport.TryRead(directory));
	}

	/// <summary>
	/// 参数必须原样翻译成命令行：空值跳过（= 当时没传，用转换器默认值），
	/// 数字用不变文化输出（小数点是点，不是逗号）。
	/// </summary>
	[Fact]
	public void SettingsBecomeArgumentsWithInvariantNumbers()
	{
		var settings = new VpkConversionSettings("merge", "metal", 1.5, null, 0.8);

		var arguments = settings.ToArguments();

		Assert.Equal(
			["--components", "merge", "--matcap", "metal", "--matcap-gain", "1.5", "--albedo-factor", "0.8"],
			arguments);
		Assert.Empty(VpkConversionSettings.Default.ToArguments());
	}

	/// <summary>
	/// 报告可以直接从 <c>vpk2patch --json</c> 的 stdout 解析：重构建拿它比"这次和上次差多少"。
	/// 逐 patch 的统计要加总（一个批次可能有多个 patch）。
	/// </summary>
	[Fact]
	public void ParsesCountsStraightFromCommandOutput()
	{
		var report = VpkConversionReport.TryParse("""
			{
			  "ok": true,
			  "patches": [
			    { "units": 7, "materials": 0, "textures": 0 },
			    { "units": 0, "materials": 30, "textures": 61 }
			  ]
			}
			""");

		Assert.NotNull(report);
		Assert.Equal(7, report!.UnitCount);
		Assert.Equal(30, report.MaterialCount);
		Assert.Equal(61, report.TextureCount);
	}

	[Fact]
	public void ParsingGarbageOutputYieldsNull()
	{
		Assert.Null(VpkConversionReport.TryParse(""));
		Assert.Null(VpkConversionReport.TryParse("不是 JSON"));
		Assert.Null(VpkConversionReport.TryParse("[1, 2]"));
	}

	/// <summary>
	/// <c>conversion.toolStamp</c>：判断"这份产物是不是当前转换器做的"。
	/// <para>
	/// 实测背景（2026-11-03）：改完转换器、也点了重构建，但有一包没被刷到，
	/// 连续三轮进游戏都在看旧产物。当时只靠文件 mtime，而 mtime 分不清
	/// "重构建过但内容没变"和"根本没重构建"。
	/// </para>
	/// </summary>
	[Fact]
	public void ToolStampTellsWhetherTheProductCameFromTheCurrentConverter()
	{
		var report = VpkConversionReport.TryParse("""
			{
			  "conversion": { "matcap": "metal", "toolStamp": "2026-11-03T04:45:00.0000000Z" },
			  "queue": [], "notes": [], "patches": []
			}
			""");

		Assert.NotNull(report);
		Assert.Equal("2026-11-03T04:45:00.0000000Z", report!.ToolStamp);

		// 同一个戳 → 是当前转换器做的。
		Assert.True(report.IsFromBuild("2026-11-03T04:45:00.0000000Z"));

		// 不同戳（转换器之后又构建过）→ 过期，建议重构建。
		Assert.False(report.IsFromBuild("2026-11-03T05:10:00.0000000Z"));

		// 任一方为 null 都判"可能过期"：误判过期只是多重建一次，
		// 误判为新则会让用户对着旧产物查半天。
		Assert.False(report.IsFromBuild(null));
		Assert.False(report.IsFromBuild(""));
	}

	[Fact]
	public void OldProductWithoutAToolStampIsNeverConsideredCurrent()
	{
		var report = VpkConversionReport.TryParse("""
			{ "conversion": { "matcap": "metal" }, "queue": [], "notes": [], "patches": [] }
			""");

		Assert.NotNull(report);
		Assert.Null(report!.ToolStamp);
		Assert.False(report.IsFromBuild("2026-11-03T04:45:00.0000000Z"));
	}
}
