using System.Buffers.Binary;
using HD2ModCore.Infrastructure;
using Xunit;
using static HD2ModCli.CliVpkRekey;

namespace HD2ModCli.Tests;

/// <summary>
/// 恒等换甲（目标 = 这个 Mod 自己的素体）写出来的生效层，必须**带上模型自带的材质层**。
/// <para>
/// <b>这条曾经是"材质丢失"的源头。</b>那一步以前只是把来源 patch 复制进 <c>Overwrite/</c>：
/// 只带 unit 层，模型自己的材质 patch（<c>patch_1</c> 起，往往带十几 MB 贴图）永远进不了
/// 生效层。而部署判定把"有内容物的 Overwrite"当成整个 Mod 的全部，于是进游戏就是
/// "模型在、材质没了"。2026-11-07 用户样本 reimu 就是这个形状：<c>Overwrite/patch_0</c>
/// 与基底 <c>patch_0</c> 字节完全相同，基底的 <c>patch_1</c> 从没进过生效层。
/// </para>
/// <para>
/// 修复后这一步走唯一的写者 <see cref="OverwriteMaterializer"/>，这里从"文件在不在"往外钉。
/// </para>
/// </summary>
public sealed class VpkRekeyIdentityOverwriteTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-rekey-identity-tests", Guid.NewGuid().ToString("N"));

	public VpkRekeyIdentityOverwriteTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public async Task IdentityRestoreKeepsTheModelsOwnMaterialPatch()
	{
		var mod = NewMod();
		var payload = Path.Combine(mod, "sectional");
		Directory.CreateDirectory(payload);
		var unit = Path.Combine(payload, $"{Archive}.patch_0");
		WriteToc(unit);
		WriteToc(Path.Combine(payload, $"{Archive}.patch_1"));
		WriteReport(mod);

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(mod);
		var result = await RestoreIdentityOverwriteAsync(
			mod, unit, overwrite, disabledSections: null, CancellationToken.None);

		// unit 层进生效层……
		Assert.Equal(Path.Combine(overwrite, $"{Archive}.patch_0"), result.UnitPatchPath);
		Assert.True(File.Exists(result.UnitPatchPath));
		// ……**模型自己的材质层也要进**：这一条就是 1.5.0 丢材质的那一步。
		Assert.True(File.Exists(Path.Combine(overwrite, $"{Archive}.patch_1")));
		// 生效层是托管形态（否则后续派生会拒绝写它），结构文件在物化之后补回来。
		Assert.True(ManagedOverwriteDirectory.IsManaged(overwrite));
		Assert.True(File.Exists(Path.Combine(overwrite, WardrobeSectionMapFile.FileName)));
	}

	/// <summary>
	/// 老库的 <c>Overwrite/</c>（P3-1 之前写的、没有归属标记、只有 unit 层）必须能被
	/// **就地认领**并重写成完整生效层，而不是"不是管理器生成的，拒绝写入"。
	/// 拒绝的结果是：重构建永远修不好这些目录，只能让用户手工删。
	/// </summary>
	[Fact]
	public async Task IdentityRestoreAdoptsALegacyOverwriteWithoutMarker()
	{
		var mod = NewMod();
		var payload = Path.Combine(mod, "sectional");
		Directory.CreateDirectory(payload);
		var unit = Path.Combine(payload, $"{Archive}.patch_0");
		WriteToc(unit);
		WriteToc(Path.Combine(payload, $"{Archive}.patch_1"));
		WriteReport(mod);

		// 老状态：Overwrite 里只有一份 unit 层，没有标记。
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(mod);
		Directory.CreateDirectory(overwrite);
		WriteToc(Path.Combine(overwrite, $"{Archive}.patch_0"));
		Assert.False(ManagedOverwriteDirectory.IsManaged(overwrite));

		var result = await RestoreIdentityOverwriteAsync(
			mod, unit, overwrite, disabledSections: null, CancellationToken.None);

		Assert.True(File.Exists(result.UnitPatchPath));
		Assert.True(File.Exists(Path.Combine(overwrite, $"{Archive}.patch_1")));
		Assert.True(ManagedOverwriteDirectory.IsManaged(overwrite));
	}

	private string NewMod()
	{
		var mod = Path.Combine(_root, "reimu");
		Directory.CreateDirectory(mod);
		return mod;
	}

	/// <summary>转换报告：模型自带的材质就是 <c>patch_1</c>（物化器的默认选择从这里来）。</summary>
	private static void WriteReport(string mod)
	{
		var json = $$"""
		{
		  "ok": true,
		  "idNamespace": "vpk-60e73603d47fe8b1",
		  "unitPatch": "{{Archive}}.patch_0",
		  "materialPatch": "{{Archive}}.patch_1",
		  "patches": [
		    {
		      "name": "reimu.vpk",
		      "kind": "model",
		      "unitPatch": "{{Archive}}.patch_0",
		      "materialPatch": "{{Archive}}.patch_1",
		      "units": 7,
		      "materials": 6,
		      "textures": 8
		    }
		  ]
		}
		""";
		File.WriteAllText(Path.Combine(mod, "vpk2patch.json"), json);
	}

	/// <summary>一份**读得出来**的空 patch TOC：扫描器只需要魔数与条目数。</summary>
	private static void WriteToc(string path)
	{
		var header = new byte[0xc0];
		BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0);
		File.WriteAllBytes(path, header);
	}
}
