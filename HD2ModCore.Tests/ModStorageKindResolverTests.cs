using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「这个 Mod 是哪一种」的唯一判定。
/// <para>
/// 以前这件事散在各处、各写各的 <c>Directory.Exists</c>，于是详情页、CLI 与重建服务
/// 对同一个目录给出不同答案——典型表现是"详情页看得见、CLI 说存储不完整"。
/// 这些用例把每种形状钉死，任何"顺手改一下判据"都会立刻失败。
/// </para>
/// </summary>
public sealed class ModStorageKindResolverTests : IDisposable
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-storage-kind-tests", Guid.NewGuid().ToString("N"));
	private readonly IModStorageKindResolver _resolver = CoreServices.CreateModStorageKindResolver();

	public ModStorageKindResolverTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewMod(string name)
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	[Fact]
	public void VpkAtRoot_IsVpk2Patch()
	{
		var dir = NewMod("vpk-mod");
		File.WriteAllText(Path.Combine(dir, "Elise_Bill.vpk"), "source");
		Directory.CreateDirectory(Path.Combine(dir, ModNodePayloadLayout.VpkPatchDirectoryName));
		File.WriteAllText(Path.Combine(dir, ModNodePayloadLayout.VpkPatchDirectoryName, PatchName), "toc");

		Assert.Equal(ModStorageKind.Vpk2Patch, _resolver.Resolve(dir));
	}

	[Fact]
	public void VpkAtRoot_Wins_EvenWhenRootStillHasAnEarlyPatch()
	{
		var dir = NewMod("vpk-with-stray-patch");
		File.WriteAllText(Path.Combine(dir, "source.vpk"), "source");
		File.WriteAllText(Path.Combine(dir, PatchName), "toc");

		Assert.Equal(ModStorageKind.Vpk2Patch, _resolver.Resolve(dir));
	}

	[Fact]
	public void RootPatchWithoutBaseLayer_IsLegacy()
	{
		var dir = NewMod("legacy");
		File.WriteAllText(Path.Combine(dir, PatchName), "toc");

		Assert.Equal(ModStorageKind.Legacy, _resolver.Resolve(dir));
	}

	[Fact]
	public void RootPatchWithBaseLayer_IsSectional()
	{
		var dir = NewMod("sectional");
		File.WriteAllText(Path.Combine(dir, PatchName), "source-toc");
		var layer = Path.Combine(dir, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(layer);
		File.WriteAllText(Path.Combine(layer, PatchName), "converted");

		Assert.Equal(ModStorageKind.Sectional, _resolver.Resolve(dir));
	}

	[Fact]
	public void BaseLayerUnderLegacyName_StillCountsAsSectional()
	{
		// 改名过渡期：老库里基底目录还叫 vpk2patch/。它同样是"有内容层"。
		var dir = NewMod("sectional-legacy-name");
		File.WriteAllText(Path.Combine(dir, PatchName), "source-toc");
		Directory.CreateDirectory(Path.Combine(dir, ModNodePayloadLayout.LegacyVpkPatchDirectoryName));

		Assert.Equal(ModStorageKind.Sectional, _resolver.Resolve(dir));
	}

	[Fact]
	public void EmptyDirectoryWithLogicalMarker_IsLogical()
	{
		var dir = NewMod("logical");
		ModNodeStorageProtocol.WriteLogicalMarker(dir, ModNodeId.New());

		Assert.Equal(ModStorageKind.Logical, _resolver.Resolve(dir));
	}

	[Fact]
	public void EmptyDirectoryWithoutMarker_IsUnknown_NotLogical()
	{
		// 空壳不能被当成合法容器，否则它会被当"逻辑 Mod"保护起来、永远不回收。
		var dir = NewMod("empty-shell");

		Assert.Equal(ModStorageKind.Unknown, _resolver.Resolve(dir));
	}

	[Fact]
	public void MissingDirectory_IsUnknown()
	{
		Assert.Equal(ModStorageKind.Unknown, _resolver.Resolve(Path.Combine(_root, "nope")));
		Assert.Equal(ModStorageKind.Unknown, _resolver.Resolve(string.Empty));
	}

	[Fact]
	public void SidecarsAreNotMistakenForPatches()
	{
		// .gpu_resources / .stream 的扩展名里也含 ".patch_" 字样，必须靠 parser 的
		// SidecarKind 判成"非 base"，否则一个只有 sidecar 的目录会被当成 legacy。
		var dir = NewMod("sidecar-only");
		File.WriteAllText(Path.Combine(dir, PatchName + ".gpu_resources"), "gpu");

		Assert.Equal(ModStorageKind.Unknown, _resolver.Resolve(dir));
	}

	[Fact]
	public void 有转换依据但内容层还空着_也算Sectional()
	{
		// 导入那一刻：来源 patch 刚平铺到根上，sectional/ 还没建。
		// 这一窗口必须已经判成 Sectional —— 否则详情页会挂上 legacy 的动作集
		// （修复 patch / 换甲 / 规整…），而那些动作对"正在等转换的条目"全是错的。
		var dir = NewMod("sectional-pending");
		File.WriteAllText(Path.Combine(dir, PatchName), "source-toc");
		WriteSourceManifest(dir);

		Assert.Equal(ModStorageKind.Sectional, _resolver.Resolve(dir));
	}

	[Fact]
	public void 来源VPK优先于转换依据()
	{
		// 两者同时存在不该发生；真遇到时 VPK 才是权威输入（重建走的就是它）。
		var dir = NewMod("vpk-and-manifest");
		File.WriteAllText(Path.Combine(dir, "Elise_Bill.vpk"), "source");
		File.WriteAllText(Path.Combine(dir, PatchName), "toc");
		WriteSourceManifest(dir);

		Assert.Equal(ModStorageKind.Vpk2Patch, _resolver.Resolve(dir));
	}

	private static void WriteSourceManifest(string modDirectory)
	{
		var document = new SectionalSourceDocument(
			SectionalSourceManifest.CurrentSchema,
			DateTimeOffset.UtcNow,
			new SectionalSourceSelection("8d3a01f4a44a3aa3", "来源护甲"),
			null,
			[new SectionalSourceEntry("0123456789abcdef0123456789abcdef", "来源条目", 0)],
			new SectionalSourceAssetKeys(["04ec4a37d9d20001"], [], []));
		Assert.Null(SectionalSourceFile.TryWrite(modDirectory, document));
	}
}
