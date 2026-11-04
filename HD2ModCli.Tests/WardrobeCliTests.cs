using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// `hd2mod wardrobe sections|compile|validate` 的命令层契约测试。
///
/// 命令层只负责三件事：选项收口、把失败翻译成退出码、失败时不落盘。
/// 语义本身在 <c>HD2ModCore</c> 里测（WardrobeSectionMapTests / WardrobeMaskCompilerTests），
/// 这里刻意只碰"CLI 行为"，不重复校验规则。
/// </summary>
[Collection("cli-console")]
public sealed class WardrobeCliTests
{
	private const string UnitA = "af431352d9b95e83";
	private const string FingerprintA0 = "a47787804c45e1a9-8852";

	[Fact]
	public async Task Wardrobe_RejectsUnrelatedOptions()
	{
		// `--confirm` 与 sections 无关，必须在**碰文件之前**被拒（所以 patch 路径可以是假的）。
		var capture = await CliTestConsole.RunCapturedAsync(
			"wardrobe", "sections", "a.patch_0", "--output", "x.json", "--confirm", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("CommandOptionNotSupported", capture.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Sections_RequiresOutputPath()
	{
		var capture = await CliTestConsole.RunCapturedAsync("wardrobe", "sections", "a.patch_0", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("OutputRequired", capture.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownWardrobeSubcommand_IsRejected()
	{
		var capture = await CliTestConsole.RunCapturedAsync("wardrobe", "paint", "--json");
		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("InvalidArguments", capture.StandardOutput, StringComparison.Ordinal);
	}

}
