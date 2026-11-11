using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「一次批量重构要动哪些 Mod」的判定：作用域展开（选中 + 递归后代）与 sectional 系列过滤。
/// <para>
/// 这两件事一旦分裂成两份实现，「一键重构所有」与「重构选中」就会给出不同目标集合，
/// 而用户看不出是哪一边错了。所以它们只有这一处，这里把它钉死。
/// </para>
/// </summary>
public sealed class RebuildTargetScopeTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-rebuild-scope-tests", Guid.NewGuid().ToString("N"));
	private readonly IModStorageKindResolver _resolver = CoreServices.CreateModStorageKindResolver();

	private static readonly Guid Host = Guid.Parse("11111111111111111111111111111111");
	private static readonly Guid Child = Guid.Parse("22222222222222222222222222222222");
	private static readonly Guid GrandChild = Guid.Parse("33333333333333333333333333333333");
	private static readonly Guid Stranger = Guid.Parse("44444444444444444444444444444444");

	public RebuildTargetScopeTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private static ModNode Node(Guid id, string name, string relativePath, bool container = false, params Guid[] hosts)
		=> new(
			new ModNodeId(id),
			relativePath,
			new ModNodeMetadata(
				name,
				Notes: null,
				CreatedUtc: DateTimeOffset.UtcNow,
				ModifiedUtc: null,
				Kind: hosts.Length == 0 ? ModNodeKind.Standard : ModNodeKind.Option,
				HostModGuids: hosts.Length == 0 ? null : hosts.Select(host => host.ToString("N")).ToArray(),
				IsLogicalContainer: container),
			PatchGroups: [],
			Children: []);

	private static LibrarySnapshot Snapshot(params ModNode[] nodes)
		=> new(1, DateTimeOffset.UtcNow, nodes.ToDictionary(node => node.Id), []);

	[Fact]
	public void 全库作用域时返回null()
	{
		Assert.Null(RebuildTargetScope.Expand(Snapshot(Node(Host, "宿主", "host")), scopeIds: null));
	}

	[Fact]
	public void 选中父条目时把递归后代一并纳入()
	{
		var snapshot = Snapshot(
			Node(Host, "宿主", "host"),
			Node(Child, "子级", "child", hosts: Host),
			Node(GrandChild, "孙级", "grand", hosts: Child),
			Node(Stranger, "无关条目", "stranger"));

		var scope = RebuildTargetScope.Expand(snapshot, [Host.ToString("N")]);

		Assert.Equal(3, scope!.Count);
		Assert.Contains(new ModNodeId(Host), scope);
		Assert.Contains(new ModNodeId(Child), scope);
		Assert.Contains(new ModNodeId(GrandChild), scope);
		Assert.DoesNotContain(new ModNodeId(Stranger), scope);
	}

	[Fact]
	public void 关系成环时不会死循环()
	{
		// 畸形元数据（互为父级）不该把遍历拖进无限循环，也不该重复输出。
		var snapshot = Snapshot(
			Node(Host, "A", "a", hosts: Child),
			Node(Child, "B", "b", hosts: Host));

		var scope = RebuildTargetScope.Expand(snapshot, [Host.ToString("N")]);

		Assert.Equal(2, scope!.Count);
	}

	[Fact]
	public void 选中多个时取并集()
	{
		var snapshot = Snapshot(
			Node(Host, "宿主", "host"),
			Node(Child, "子级", "child", hosts: Host),
			Node(Stranger, "无关条目", "stranger"));

		var scope = RebuildTargetScope.Expand(snapshot, [Host.ToString("N"), Stranger.ToString("N")]);

		Assert.Equal(3, scope!.Count);
	}

	[Fact]
	public void 无效或已不存在的guid被忽略()
	{
		var snapshot = Snapshot(Node(Host, "宿主", "host"));

		Assert.Empty(RebuildTargetScope.Expand(snapshot, ["不是 guid", Guid.NewGuid().ToString("N")])!);
	}

	[Fact]
	public void 空壳容器自身进作用域但不算sectional系列()
	{
		var dir = Path.Combine(_root, "container");
		Directory.CreateDirectory(dir);

		Assert.False(RebuildTargetScope.IsSectionalSeries(dir, _resolver));
	}

	[Theory]
	[InlineData(true, false)]   // vpk2patch：根上有来源 .vpk
	[InlineData(false, true)]   // sectional：根上有来源 patch + 转换依据
	public void 两档sectional系列都算目标(bool withVpk, bool withManifest)
	{
		var dir = Path.Combine(_root, $"mod-{withVpk}-{withManifest}");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), "toc");
		if (withVpk) File.WriteAllText(Path.Combine(dir, "source.vpk"), "vpk");
		if (withManifest)
		{
			var document = new SectionalSourceDocument(
				SectionalSourceManifest.CurrentSchema,
				DateTimeOffset.UtcNow,
				new SectionalSourceSelection("8d3a01f4a44a3aa3", "来源护甲"),
				null,
				[new SectionalSourceEntry("0123456789abcdef0123456789abcdef", "来源条目", 0)],
				new SectionalSourceAssetKeys(["04ec4a37d9d20001"], [], []));
			Assert.Null(SectionalSourceFile.TryWrite(dir, document));
		}

		Assert.True(RebuildTargetScope.IsSectionalSeries(dir, _resolver));
	}

	[Fact]
	public void legacy与空目录都不算目标()
	{
		var legacy = Path.Combine(_root, "legacy");
		Directory.CreateDirectory(legacy);
		File.WriteAllText(Path.Combine(legacy, "9ba626afa44a3aa3.patch_0"), "toc");

		Assert.False(RebuildTargetScope.IsSectionalSeries(legacy, _resolver));
		Assert.False(RebuildTargetScope.IsSectionalSeries(Path.Combine(_root, "missing"), _resolver));
		Assert.False(RebuildTargetScope.IsSectionalSeries(string.Empty, _resolver));
	}
}
