using System.Buffers.Binary;
using System.Text;
using HD2SourceMigration.VpkConversion;
using Xunit;

namespace HD2SourceMigration.Tests;

/// <summary>
/// <see cref="SourceMaterialSource.ScopedTo"/> 的语义：<b>自身优先、整批兜底</b>。
/// <para>
/// 这两条看起来矛盾，其实各管一件事：
/// </para>
/// <list type="bullet">
/// <item><b>自身优先</b>决定"<b>读到</b>的是谁那份"——B1 与 B2 同名同路径的 53 个 <c>.vmt</c>
/// 如果先入队的赢，B2 的 patch 就会是空的（或者更糟：装着 B1 的内容）。</item>
/// <item><b>整批兜底</b>决定"<b>能不能读到</b>"——A1 的 <c>white.vmt</c> 指向的 <c>white.vtf</c>
/// 只存在于 D1，没有兜底就一个材质都造不出来。</item>
/// </list>
/// <para>
/// 所以"写不写进 patch"与"读不读得到"是两件事：前者看 <see cref="SourceMaterialSource.OwnsVmt"/> /
/// <see cref="SourceMaterialSource.OwnsVtf"/>，后者看 <c>TryReadVmt</c> / <c>TryReadVtf</c>。
/// </para>
/// </summary>
public sealed class VpkMaterialScopeTests : IDisposable
{
	private readonly List<string> temporaryFiles = [];

	public void Dispose()
	{
		foreach (var path in temporaryFiles)
		{
			try
			{
				File.Delete(path);
			}
			catch (IOException)
			{
				// 测试清理失败不该让测试结果变红。
			}
		}
	}

	[Fact]
	public void ScopedViewReadsItsOwnCopyOfANameSharedWithAnEarlierVpk()
	{
		// 两个包里有**同名同路径**的 white.vmt，内容不同——正是 B1/B2 的真实形状。
		var first = WriteVpk("b1", ("white.vmt", "materials/lunalice", "B1"), ("white.vtf", "materials/lunalice", "B1"));
		var second = WriteVpk("b2", ("white.vmt", "materials/lunalice", "B2"), ("white.vtf", "materials/lunalice", "B2"));

		var batch = SourceMaterialSource.Open([first, second]);
		// 整批视图：先入队的赢（这是确定行为，不是任意行为）。
		Assert.True(batch.TryReadVmt("white", out var fromBatch));
		Assert.Equal("B1", Encoding.UTF8.GetString(fromBatch));
		Assert.Null(batch.PrimaryVpkPath);
		Assert.Empty(batch.OwnedVmtNames);

		// 作用域视图：**自己那份**。
		var scoped = batch.ScopedTo(second);
		Assert.Equal(Path.GetFullPath(second), scoped.PrimaryVpkPath);
		Assert.True(scoped.TryReadVmt("white", out var fromScope));
		Assert.Equal("B2", Encoding.UTF8.GetString(fromScope));
		Assert.True(scoped.TryReadVtf("white", out var textureFromScope));
		Assert.Equal("B2", Encoding.UTF8.GetString(textureFromScope));

		Assert.True(scoped.OwnsVmt("white"));
		Assert.True(scoped.OwnsVtf("white"));
		Assert.True(scoped.TryGetVmtSource("white", out var vmtVpk, out _));
		Assert.Equal(Path.GetFullPath(second), vmtVpk);
	}

	[Fact]
	public void ScopedViewStillResolvesNamesOnlyTheBatchHas()
	{
		// A1 的形状：有 white.vmt，但没有 white.vtf（那张只在 D1 里）。
		var a1 = WriteVpk("a1", ("white.vmt", "materials/lunalice", "A1"), ("shinano_body.vtf", "materials/lunalice", "A1body"));
		var d1 = WriteVpk("d1", ("white.vtf", "materials/lunalice", "D1white"));

		var batch = SourceMaterialSource.Open([a1, d1]);
		var scoped = batch.ScopedTo(a1);

		// 读得到——靠兜底。
		Assert.True(scoped.TryReadVtf("white", out var bytes));
		Assert.Equal("D1white", Encoding.UTF8.GetString(bytes));

		// 但不属于它——所以**不该写进它的 patch**。
		Assert.False(scoped.OwnsVtf("white"));
		Assert.True(scoped.OwnsVmt("white"));
		Assert.True(scoped.OwnsVtf("shinano_body"));

		// 报出来的来源是真正提供它的那个包。
		Assert.True(scoped.TryGetVtfSource("white", out var vtfVpk, out var entryPath));
		Assert.Equal(Path.GetFullPath(d1), vtfVpk);
		Assert.Contains("white.vtf", entryPath, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ScopedViewOfAVpkWithNoMaterialsOwnsNothing()
	{
		// 模型 VPK 的形状：只有 .mdl，没有 .vmt/.vtf。
		var model = WriteVpk("model", ("survivor.mdl", "models/survivors", "MDL"));
		var materials = WriteVpk("mat", ("white.vmt", "materials/lunalice", "white"));

		var batch = SourceMaterialSource.Open([model, materials]);
		var scoped = batch.ScopedTo(model);

		Assert.Empty(scoped.OwnedVmtNames);
		Assert.Empty(scoped.OwnedVtfNames);
		Assert.False(scoped.OwnsVmt("white"));
		// 兜底仍然在：模型 patch 要靠它算 Unit 引用的材质 id。
		Assert.True(scoped.ContainsVmt("white"));
	}

	[Fact]
	public void ScopedViewOfAMissingVpkReportsTheFailureInsteadOfThrowing()
	{
		var materials = WriteVpk("mat", ("white.vmt", "materials/lunalice", "white"));
		var batch = SourceMaterialSource.Open([materials]);

		var scoped = batch.ScopedTo(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.vpk"));

		Assert.Empty(scoped.OwnedVmtNames);
		Assert.NotEmpty(scoped.Failures);
		// 兜底还在，所以"读不到自己的"不会让整批失效。
		Assert.True(scoped.ContainsVmt("white"));
	}

	[Fact]
	public void BatchViewHasNoPrimarySoNothingIsOwned()
	{
		var materials = WriteVpk("mat", ("white.vmt", "materials/lunalice", "white"));
		var batch = SourceMaterialSource.Open([materials]);

		Assert.Null(batch.PrimaryVpkPath);
		Assert.False(batch.OwnsVmt("white"));
		Assert.False(batch.OwnsVtf("white"));
		Assert.True(batch.ContainsVmt("white"));
	}

	// ---------------------------------------------------------------- 夹具

	private string WriteVpk(string stem, params (string Name, string Directory, string Content)[] files)
	{
		var path = Path.Combine(Path.GetTempPath(), $"hd2scope-{stem}-{Guid.NewGuid():N}.vpk");
		File.WriteAllBytes(path, BuildVpk(files));
		temporaryFiles.Add(path);
		return path;
	}

	/// <summary>
	/// 造一个 VPK v1（18 字节条目）。布局必须与 <c>VpkArchive</c> 期望的一致：
	/// 扩展名 → 目录 → 文件名三层嵌套，各自用空串终止，条目头后面紧跟 preload 字节。
	/// </summary>
	private static byte[] BuildVpk((string Name, string Directory, string Content)[] files)
	{
		const int headerSize = 12;
		const int entrySize = 18;

		var payloads = new List<byte>();
		var offsets = new int[files.Length];
		var split = new (string Extension, string Directory, string Stem)[files.Length];
		for (var index = 0; index < files.Length; index++)
		{
			var name = files[index].Name;
			var dot = name.LastIndexOf('.');
			split[index] = (
				dot < 0 ? " " : name[(dot + 1)..],
				files[index].Directory,
				dot < 0 ? name : name[..dot]);
			offsets[index] = payloads.Count;
			payloads.AddRange(Encoding.UTF8.GetBytes(files[index].Content));
		}

		var tree = new List<byte>();
		foreach (var extensionGroup in split.Select((item, index) => (item, index)).GroupBy(pair => pair.item.Extension))
		{
			WriteString(tree, extensionGroup.Key);
			foreach (var directoryGroup in extensionGroup.GroupBy(pair => pair.item.Directory))
			{
				WriteString(tree, directoryGroup.Key);
				foreach (var (item, index) in directoryGroup)
				{
					WriteString(tree, item.Stem);
					var header = new byte[entrySize];
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0);
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x7FFF);
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)offsets[index]);
					BinaryPrimitives.WriteUInt32LittleEndian(
						header.AsSpan(12), (uint)Encoding.UTF8.GetByteCount(files[index].Content));
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 0xFFFF);
					tree.AddRange(header);
				}

				WriteString(tree, string.Empty);
			}

			WriteString(tree, string.Empty);
		}

		WriteString(tree, string.Empty);

		var result = new byte[headerSize + tree.Count + payloads.Count];
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x55AA1234);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)tree.Count);
		tree.CopyTo(result, headerSize);
		payloads.CopyTo(result, headerSize + tree.Count);
		return result;
	}

	private static void WriteString(List<byte> buffer, string value)
	{
		buffer.AddRange(Encoding.UTF8.GetBytes(value));
		buffer.Add(0);
	}
}
