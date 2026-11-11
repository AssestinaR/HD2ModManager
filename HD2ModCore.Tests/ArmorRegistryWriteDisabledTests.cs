using System.Security.Cryptography;
using FileDecryptor;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 真映射**停写**护栏（2026-11-11）。
///
/// <para>
/// 背景：游戏这一版改了护甲注册表文件（<c>generated_customization_armor_sets.dl_bin</c>）
/// 的校验方式。文件本身仍能正常解密、格式没变、写进去也不报错——但**写过的文件一进游戏就闪退**。
/// 实测记录见 <c>docs/agent/dl-bin-write-freeze-20261111.md</c>。
/// </para>
///
/// <para>
/// 所以这里钉住的不是"怎么改文件"，而是"**绝不改文件**"：写入被拒、错误可读、
/// 磁盘上一个字节都没动。注意 <c>mappings</c> 为空的"还原"调用也在这条护栏之内——
/// 还原同样是写，同样会闪退。
/// </para>
/// <para>
/// <b>为什么要单独一个 collection。</b>放开写入是进程级计数（xUnit 默认按集合并行），
/// 这个类断言的是"计数为 0 时的行为"。放进同一个 collection 才能保证跑它的时候
/// 没有别的测试类正开着写入，否则会读到别人的状态而假红。
/// </para>
/// </summary>
[Collection(ArmorRegistryWriteGateCollection.Name)]
public sealed class ArmorRegistryWriteDisabledTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-dlbin-freeze-" + Guid.NewGuid().ToString("N"));

	public ArmorRegistryWriteDisabledTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
	}

	private string GameDataDirectory
	{
		get
		{
			var directory = Path.Combine(_root, "data");
			Directory.CreateDirectory(Path.Combine(directory, "game"));
			return directory;
		}
	}

	private string BackupDirectory => Path.Combine(_root, "backup");

	/// <summary>写一份能解开的密封文件，返回它的 SHA-256。</summary>
	private string SeedTarget(out string path)
	{
		path = ArmorRegistryDataMapper.ResolveTargetPath(GameDataDirectory);
#pragma warning disable CS0618
		var decryptor = Decryptor.CreateWithEmbeddedKeys();
#pragma warning restore CS0618
		var plain = SyntheticPlain();
		var sealedBytes = decryptor.EncodeData(plain);
		if (File.Exists(path)) File.Delete(path);
		File.WriteAllBytes(path, sealedBytes);
		return Convert.ToHexString(SHA256.HashData(sealedBytes));
	}

	/// <summary>最小可解析结构：头 4 字节 + 一个 LDLD 块，块里 0 条套件。</summary>
	private static byte[] SyntheticPlain()
	{
		const int headerSize = 24;
		var block = new byte[headerSize];
		"LDLD"u8.CopyTo(block.AsSpan(4));
		var plain = new byte[4 + headerSize];
		BitConverter.GetBytes(0xA1u).CopyTo(plain, 0);
		block.CopyTo(plain.AsSpan(4));
		return plain;
	}

	[Fact]
	public async Task WriteIsRefusedEvenWhenMappingsAreProvided()
	{
		var before = SeedTarget(out var path);

		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			GameDataDirectory,
			[new ArmorRegistryDataMapping(100, 200)],
			BackupDirectory,
			CancellationToken.None);

		Assert.False(result.Written);
		Assert.Equal(0, result.Applied);
		Assert.False(string.IsNullOrWhiteSpace(result.Message));
		// 上层会拼成「真映射：{Message}」，所以消息本身不该再以「真映射」开头。
		Assert.Contains("已暂停写入", result.Message!);
		Assert.DoesNotContain("真映射已暂停", result.Message!);
		Assert.Equal(before, Sha256(path));
	}

	/// <summary>
	/// 最容易被漏掉的一条：**还原也是写**。「离开真映射模式要还原注册表」这条老逻辑
	/// 会以空映射调用到这里，如果不拦，用户想"安全回退"的动作本身就是那次闪退。
	/// </summary>
	[Fact]
	public async Task RestoreWithEmptyMappingsIsAlsoRefused()
	{
		var before = SeedTarget(out var path);

		var result = await new ArmorRegistryDataMapper().ApplyAsync(
			GameDataDirectory,
			Array.Empty<ArmorRegistryDataMapping>(),
			BackupDirectory,
			CancellationToken.None);

		Assert.False(result.Written);
		Assert.Equal(before, Sha256(path));
	}

	[Fact]
	public async Task NoBackupCopyIsCreatedWhileFrozen()
	{
		SeedTarget(out _);

		await new ArmorRegistryDataMapper().ApplyAsync(
			GameDataDirectory,
			[new ArmorRegistryDataMapping(100, 200)],
			BackupDirectory,
			CancellationToken.None);

		// 停写期间连"原件副本"都不该落盘：那份副本在我们不再写之后没有任何用途，
		// 而且它的存在会让下一次恢复时误以为基底可信。
		Assert.False(Directory.Exists(BackupDirectory) && Directory.EnumerateFiles(BackupDirectory).Any());
	}

	[Fact]
	public void FreezeFlagMatchesTheAnnouncedState()
	{
		// 这条是自我检查：护栏之所以有效全靠这个标志。哪天有人把它翻回 true，
		// 上面几条会一起红 —— 这正是想要的信号（必须同时更新事故文档与发版说明）。
		Assert.False(ArmorMappingModes.TrueMappingWritable);
		Assert.False(ArmorMappingModes.CanWriteRegistryData);
	}

	private static string Sha256(string path)
		=> Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
