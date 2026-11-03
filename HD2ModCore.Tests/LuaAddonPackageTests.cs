using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 内置 Lua 功能的外置包：两种形态（lua 版 / patch 版）+ addon 约定。
///
/// 契约来自三家加载器的一手资料（2026-11-01 核对）：
/// Boot Mod Loader / Chef Shared Loader / MDL3 全都靠
/// "资源名 mods/&lt;作者&gt;/&lt;名字&gt; + 首行 -- HD2-Addon: 同名" 来识别 addon。
/// 这里把这条约定钉死，免得哪天改导出格式悄悄让所有加载器都扫不到。
/// </summary>
public sealed class LuaAddonPackageTests
{
	private static string NewTempRoot()
	{
		var dir = Path.Combine(Path.GetTempPath(), "hd2lua-pkg-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	[Fact]
	public void Physics_SourceStartsWithTheAddonMarkerOnLineOne()
	{
		var feature = LuaAddonCatalog.Physics();
		// 首行必须是标记：Boot Mod Loader 只认第一行，前面有任何空行/注释都会漏掉。
		Assert.StartsWith("-- HD2-Addon: mods/hd2modmanager/physbone\n", feature.Source, StringComparison.Ordinal);
		// 不能有 BOM：Lua 5.1 不跳过 BOM，带 BOM 的脚本编译会直接失败。
		Assert.False(feature.Source.Length > 0 && feature.Source[0] == '\uFEFF');
	}

	[Fact]
	public void Wardrobe_SourceStartsWithTheAddonMarkerOnLineOne()
	{
		var feature = LuaAddonCatalog.Wardrobe();
		Assert.StartsWith("-- HD2-Addon: mods/hd2modmanager/wardrobe\n", feature.Source, StringComparison.Ordinal);
	}

	[Fact]
	public void Physics_SourceCarriesInlinedCompatAndAutoInstall()
	{
		var source = LuaAddonCatalog.Physics().Source;
		Assert.Contains("local PhysBoneCompat = (function()", source, StringComparison.Ordinal);
		Assert.Contains("local PhysBoneRuntime = (function()", source, StringComparison.Ordinal);
		Assert.Contains("PhysBoneRuntime.install(", source, StringComparison.Ordinal);
		Assert.Contains("rawget(_G, 'stingray')", source, StringComparison.Ordinal);
	}

	/// <summary>
	/// 求解器必须能**从生成器传进运行时**，否则"切到粒子链"只是一个不起作用的设置项。
	///
	/// 2026-11-03 之前这条链是断的：`attachOptions` 不传 solver、`update_tip` 又把所有
	/// 非 "angle" 的值都送进 tip 核，所以设了 `chain` 也会静默跑旧核。
	/// </summary>
	[Theory]
	[InlineData(PhysicsSolvers.Chain)]
	[InlineData(PhysicsSolvers.Tip)]
	public void Physics_ComposedSourceCarriesTheChosenSolver(string solver)
	{
		var source = PhysBoneLuaSource.ComposeStandalonePackage("mods/t/x", solver: solver);
		Assert.Contains($"solver = '{solver}'", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Physics_ComposedSourceOmitsSolverWhenNotSpecified()
	{
		// 不指定就不写 —— 让运行时用自己的默认值，避免生成器与运行时两处各自演化。
		//
		// ⚠️ 只能检查**install 那一行**。整份源码里 `solver =` 会出现很多次
		// （compat 内联代码里有 `solver = options.solver or "tip"`），
		// 拿整份源码做 DoesNotContain 会必定失败。
		var installLine = PhysBoneLuaSource.ComposeStandalonePackage("mods/t/x")
			.Split('\n')
			.Single(line => line.Contains("PhysBoneRuntime.install(", StringComparison.Ordinal));
		Assert.DoesNotContain("solver", installLine, StringComparison.Ordinal);
	}

	[Fact]
	public void Physics_CatalogUsesTheCurrentlyConfiguredSolver()
	{
		var previous = LuaDeploymentService.PhysicsSolver;
		try
		{
			LuaDeploymentService.PhysicsSolver = PhysicsSolvers.Chain;
			Assert.Contains("solver = 'chain'", LuaAddonCatalog.Physics().Source, StringComparison.Ordinal);
			LuaDeploymentService.PhysicsSolver = PhysicsSolvers.Tip;
			Assert.Contains("solver = 'tip'", LuaAddonCatalog.Physics().Source, StringComparison.Ordinal);
		}
		finally
		{
			LuaDeploymentService.PhysicsSolver = previous;
		}
	}

	[Fact]
	public void PhysicsSolvers_NormalizeFallsBackToTheVerifiedCore()
	{
		Assert.Equal(PhysicsSolvers.Tip, PhysicsSolvers.Normalize(null));
		Assert.Equal(PhysicsSolvers.Tip, PhysicsSolvers.Normalize("nonsense"));
		Assert.Equal(PhysicsSolvers.Chain, PhysicsSolvers.Normalize(PhysicsSolvers.Chain));
		Assert.Equal(PhysicsSolvers.Tip, PhysicsSolvers.Default);
	}

	[Fact]
	public void Export_ProducesBothLuaAndPatchVariants()
	{
		var root = NewTempRoot();
		try
		{
			var folder = LuaAddonPackageExport.Export(root, LuaAddonCatalog.Physics());

			// lua 版：一个裸脚本
			var luaFile = Path.Combine(folder, "lua版", "hd2physbone.lua");
			Assert.True(File.Exists(luaFile), luaFile);

			// patch 版：manifest + 补丁三件套
			var patchDir = Path.Combine(folder, "patch版");
			Assert.True(File.Exists(Path.Combine(patchDir, "manifest.json")));
			Assert.True(File.Exists(Path.Combine(patchDir, "9ba626afa44a3aa3.patch_0")));
			Assert.True(File.Exists(Path.Combine(patchDir, "9ba626afa44a3aa3.patch_0.stream")));
			Assert.True(File.Exists(Path.Combine(patchDir, "9ba626afa44a3aa3.patch_0.gpu_resources")));

			Assert.True(File.Exists(Path.Combine(folder, LuaAddonPackageExport.ReadmeFileName)));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Export_IsIdempotentAndDropsStaleFiles()
	{
		var root = NewTempRoot();
		try
		{
			var folder = LuaAddonPackageExport.Export(root, LuaAddonCatalog.Physics());
			var stale = Path.Combine(folder, "lua版", "stale.lua");
			File.WriteAllText(stale, "-- leftover");

			LuaAddonPackageExport.Export(root, LuaAddonCatalog.Physics());
			Assert.False(File.Exists(stale), "重复导出必须整目录重建，否则残留文件会被当成要装的东西");
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Export_PatchArchiveContainsOneLuaResourceNamedAfterTheAddon()
	{
		var root = NewTempRoot();
		try
		{
			var folder = LuaAddonPackageExport.Export(root, LuaAddonCatalog.Physics());
			var path = Path.Combine(folder, "patch版", "9ba626afa44a3aa3.patch_0");
			var bytes = File.ReadAllBytes(path);

			// 头部：magic + typeCount=1 + fileCount=1
			Assert.Equal(0xF0000011u, BitConverter.ToUInt32(bytes, 0));
			Assert.Equal(1u, BitConverter.ToUInt32(bytes, 4));
			Assert.Equal(1u, BitConverter.ToUInt32(bytes, 8));

			// 条目里的 fileId 必须是 addon 名的 Stingray 哈希——加载器按名字找它。
			var expected = HD2GameItemAssets.StingrayHash.Murmur64A(LuaAddonCatalog.PhysicsAddonId);
			Assert.Equal(expected, BitConverter.ToUInt64(bytes, 104));
			// 类型 id = Lua
			Assert.Equal(0xA14E8DFA2CD117E2ul, BitConverter.ToUInt64(bytes, 112));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Export_TwoFeaturesLandInSiblingFoldersUnderTheSameRoot()
	{
		var root = NewTempRoot();
		try
		{
			LuaAddonPackageExport.Export(root, LuaAddonCatalog.Physics());
			LuaAddonPackageExport.Export(root, LuaAddonCatalog.Wardrobe());

			Assert.True(Directory.Exists(Path.Combine(root, "physbone")));
			Assert.True(Directory.Exists(Path.Combine(root, "wardrobe")));
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void FeatureGuidsAreStableAndDistinct()
	{
		// GUID 固定：重复导出不能被管理器当成另一个 mod。
		Assert.NotEqual(LuaAddonCatalog.PhysicsModGuid, LuaAddonCatalog.WardrobeModGuid);
		Assert.True(Guid.TryParse(LuaAddonCatalog.PhysicsModGuid, out _));
		Assert.True(Guid.TryParse(LuaAddonCatalog.WardrobeModGuid, out _));
	}
}
