using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 头身合并在**编译期**落地：改的是 <c>Overwrite/custom-model.json</c>（唯一生效层），
/// <c>sectional/</c> 里那份是权威留档、一个字节都不许动。
/// <para>
/// 规则（用户 2026-11-08 定的）：把头 unit 的**注册件**从 <c>slot 0 / layer 0</c>
/// 移到 <c>slot 2 / layer 1</c>，unit id 不变，另留 <c>movedFrom</c>。
/// </para>
/// </summary>
public sealed class CompileHeadMergeTests
{
	private const string Archive = "9ba626afa44a3aa3";

	[Fact]
	public void WritesTheMergedRegistryIntoTheEffectiveLayer()
	{
		using var fixture = new ModFixture();
		var sectionalRegistry = Path.Combine(fixture.ModDirectory, "sectional", "custom-model.json");
		var before = File.ReadAllText(sectionalRegistry);

		var note = ModCompileService.WriteMergedCustomModel(fixture.ModDirectory, []);

		Assert.NotNull(note);
		// 权威留档没被碰过。
		Assert.Equal(before, File.ReadAllText(sectionalRegistry));

		var merged = JsonNode.Parse(File.ReadAllText(
			Path.Combine(fixture.ModDirectory, "Overwrite", "custom-model.json")))!;
		var head = merged["pieces"]!.AsArray()
			.OfType<JsonObject>()
			.Single(piece => piece["unit"]!.GetValue<string>() == "aaaa0001");

		Assert.Equal(2, head["slot"]!.GetValue<int>());          // 胸部槽位
		Assert.Equal(1, head["pieceType"]!.GetValue<int>());     // 第 1 层
		Assert.Equal("slot0/layer0", head["movedFrom"]!.GetValue<string>());
		Assert.Equal("aaaa0001", head["unit"]!.GetValue<string>());// unit id 不变
	}

	[Fact]
	public void DoesNotMoveAHeadIntoAnOccupiedLayer()
	{
		using var fixture = new ModFixture(occupiedChestLayer: 1);
		var note = ModCompileService.WriteMergedCustomModel(fixture.ModDirectory, []);

		Assert.NotNull(note);   // 仍然合并，只是换到下一层
		var head = JsonNode.Parse(File.ReadAllText(
				Path.Combine(fixture.ModDirectory, "Overwrite", "custom-model.json")))!["pieces"]!
			.AsArray().OfType<JsonObject>()
			.Single(piece => piece["unit"]!.GetValue<string>() == "aaaa0001");
		Assert.Equal(2, head["slot"]!.GetValue<int>());
		Assert.Equal(2, head["pieceType"]!.GetValue<int>());
	}

	// 生效层优先：收集器读的是 Overwrite 里那份，不是 sectional 的留档。
	[Fact]
	public void CatalogPrefersTheEffectiveLayer()
	{
		using var fixture = new ModFixture();
		ModCompileService.WriteMergedCustomModel(fixture.ModDirectory, []);
		var modsRoot = Path.GetDirectoryName(fixture.ModDirectory)!;

		var collected = CustomModelCatalog.Collect(modsRoot);

		var model = Assert.Single(collected.Models);
		var head = Assert.Single(model.Pieces!, piece => piece.Unit == "aaaa0001");
		Assert.Equal(2u, head.Slot);
		Assert.Equal(1u, head.PieceType);
	}

	/// <summary>一个最小可用的 Mod 目录：sectional 里有基底 patch 与素体注册表。</summary>
	private sealed class ModFixture : IDisposable
	{
		public ModFixture(int? occupiedChestLayer = null)
		{
			Root = Path.Combine(Path.GetTempPath(), "hd2-compile-tests", Guid.NewGuid().ToString("N"));
			ModDirectory = Path.Combine(Root, "mod");
			var sectional = Path.Combine(ModDirectory, "sectional");
			Directory.CreateDirectory(sectional);
			File.WriteAllBytes(Path.Combine(sectional, Archive + ".patch_0"), [0x11, 0x22, 0x33]);

			var pieces = new JsonArray
			{
				new JsonObject
				{
					["body"] = 3, ["unit"] = "aaaa0001", ["slot"] = 0, ["pieceType"] = 0,
					["tone"] = 0, ["enabled"] = true,
				},
				new JsonObject
				{
					["body"] = 0, ["unit"] = "aaaa0002", ["slot"] = 2, ["pieceType"] = 0,
					["tone"] = 0, ["enabled"] = true, ["isDefaultBase"] = true,
				},
			};
			if (occupiedChestLayer is { } layer)
			{
				pieces.Add(new JsonObject
				{
					["body"] = 3, ["unit"] = "aaaa0003", ["slot"] = 2, ["pieceType"] = layer,
					["tone"] = 0, ["enabled"] = true,
				});
			}

			var document = new JsonObject
			{
				["key"] = "custom-testkey",
				["name"] = "测试素体",
				["pieces"] = pieces,
			};
			File.WriteAllText(
				Path.Combine(sectional, "custom-model.json"),
				document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		}

		public string Root { get; }

		public string ModDirectory { get; }

		public void Dispose()
		{
			try { Directory.Delete(Root, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}
}
