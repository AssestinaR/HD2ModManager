using System.Buffers.Binary;
using System.Text;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.Lua;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class LuaPatchComposerTests
{
	[Fact]
	public async Task InspectAndCompose_RoundTripsSingleAndMergedBootPatches()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lua-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var firstPath = Path.Combine(root, "first.patch_0");
			var secondPath = Path.Combine(root, "second.patch_0");
			await File.WriteAllBytesAsync(firstPath, BuildPatch("First", "print('first')"));
			await File.WriteAllBytesAsync(secondPath, BuildPatch("Second", "print('second')"));

			var inspector = new LuaPatchInspector();
			var first = await inspector.InspectAsync(firstPath);
			var second = await inspector.InspectAsync(secondPath);
			Assert.Equal(LuaPatchWrapperKind.Single, first.WrapperKind);
			Assert.Equal(LuaPatchWrapperKind.Single, second.WrapperKind);
			Assert.Single(first.Blocks);
			Assert.Single(second.Blocks);

			var composition = new LuaPatchComposer().Compose([first, second]);
			Assert.Equal(2, composition.Blocks.Count);
			Assert.Equal(2, composition.Ledger.Blocks.Count);
			Assert.Contains("--@@HD2LUA-BEGIN 1", composition.SourceText, StringComparison.Ordinal);
			Assert.Contains("print('first')", composition.SourceText, StringComparison.Ordinal);
			Assert.Contains("print('second')", composition.SourceText, StringComparison.Ordinal);

			var output = Path.Combine(root, "merged.patch_0");
			await new LuaPatchComposer().WriteAsync(output, composition);
			var merged = await inspector.InspectAsync(output);
			Assert.Equal(LuaPatchWrapperKind.Hd2LuaMergeV1, merged.WrapperKind);
			Assert.Equal(2, merged.Blocks.Count);
			Assert.Equal(composition.Ledger.Blocks[0].BodySha256, merged.Blocks[0].BodySha256);
			Assert.Equal(composition.Ledger.Blocks[1].BodySha256, merged.Blocks[1].BodySha256);
			Assert.True(File.Exists(output + ".stream"));
			Assert.True(File.Exists(output + ".gpu_resources"));
			Assert.Equal(0, new FileInfo(output + ".stream").Length);
			Assert.Equal(0, new FileInfo(output + ".gpu_resources").Length);

			var bytes = await File.ReadAllBytesAsync(output);
			Assert.Equal((uint)(bytes.Length - 0xC0), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0xB8, 4)));
			Assert.Equal((ulong)(bytes.Length - 0xC0 + 8), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0xA0, 8)));
			// The template still carries the legacy source version, but what we emit
			// must always be the bytecode version the current game accepts.
			Assert.Equal(LuaResourceFormat.BytecodeVersion, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0xBC, 4)));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task Inspect_RejectsDifferentBootBytecodeDuringCompose()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lua-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var firstPath = Path.Combine(root, "first.patch_0");
			var secondPath = Path.Combine(root, "second.patch_0");
			await File.WriteAllBytesAsync(firstPath, BuildPatch("First", "print('first')", [27, 76, 74, 2]));
			await File.WriteAllBytesAsync(secondPath, BuildPatch("Second", "print('second')", [27, 76, 74, 3]));
			var inspector = new LuaPatchInspector();
			var first = await inspector.InspectAsync(firstPath);
			var second = await inspector.InspectAsync(secondPath);

			Assert.Throws<LuaPatchFormatException>(() => new LuaPatchComposer().Compose([first, second]));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// A post-update boot patch carries a compiled chunk. It cannot be spliced into
	// a text merge, so the composer has to replay it whole -- including when it is
	// the only input, which is the common case for a single Lua mod.
	[Fact]
	public async Task Inspect_BytecodePayload_IsReplayedInsteadOfBeingRejected()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lua-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var path = Path.Combine(root, "bytecode.patch_0");
			var chunk = Lua51WrapperChunk.Build("print('from bytecode')");
			await File.WriteAllBytesAsync(path, BuildPatchFromPayload(chunk, LuaResourceFormat.BytecodeVersion));

			var inspector = new LuaPatchInspector();
			var document = await inspector.InspectAsync(path);
			Assert.Equal(LuaPatchWrapperKind.Bytecode, document.WrapperKind);
			Assert.Equal(chunk, document.BootBytecode);
			Assert.Empty(document.Blocks);

			var composition = new LuaPatchComposer().ComposeOpaque([document]);
			Assert.Contains("local BC = \"", composition.SourceText, StringComparison.Ordinal);
			Assert.Contains("if chunk then boot_ok, boot_ret = pcall(chunk) end", composition.SourceText, StringComparison.Ordinal);
			Assert.EndsWith("return __hd2_opaque_runner_1(...)" + Environment.NewLine, composition.SourceText, StringComparison.Ordinal);

			// A truncated chunk must be rejected rather than replayed.
			var truncated = Path.Combine(root, "truncated.patch_0");
			await File.WriteAllBytesAsync(truncated, BuildPatchFromPayload(chunk[..^4], LuaResourceFormat.BytecodeVersion));
			await Assert.ThrowsAsync<LuaPatchFormatException>(() => inspector.InspectAsync(truncated).AsTask());
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task Inspect_RealReferenceSamples_WhenPresent()
	{
		var workspace = Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.FullName;
		var samples = new[]
		{
			Path.Combine(workspace, "tmp", "lua_samples", "physbone", "9ba626afa44a3aa3.patch_0"),
			Path.Combine(workspace, "tmp", "lua_samples", "weaponhud", "Ammo Icon", "9ba626afa44a3aa3.patch_0"),
			Path.Combine(workspace, "tmp", "lua_samples", "weaponhud", "Ammo Icon Enhanced", "9ba626afa44a3aa3.patch_0"),
		};
		var existing = samples.Where(File.Exists).ToArray();
		if (existing.Length == 0) return;

		var inspector = new LuaPatchInspector();
		var documents = new List<LuaPatchDocument>();
		foreach (var sample in existing)
		{
			var document = await inspector.InspectAsync(sample);
			Assert.Equal(LuaAssetKeys.BootAssetKey, document.Entry.AssetKey);
			Assert.NotEmpty(document.BootBytecode);
			Assert.NotEmpty(document.Blocks);
			documents.Add(document);
		}

		if (documents.Count >= 2)
		{
			var composition = new LuaPatchComposer().Compose(documents);
			Assert.Equal(documents.Sum(document => document.Blocks.Count), composition.Blocks.Count);
		}
	}

	[Fact]
	public async Task ComposeOpaque_WrapsCompleteChunksAndPreservesLastReturn()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-lua-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var firstPath = Path.Combine(root, "first.patch_0");
			var secondPath = Path.Combine(root, "second.patch_0");
			await File.WriteAllBytesAsync(firstPath, BuildPatch("First", "return { first = true }"));
			await File.WriteAllBytesAsync(secondPath, BuildPatch("Second", "return { second = true }"));
			var inspector = new LuaPatchInspector();
			var first = await inspector.InspectOpaqueAsync(firstPath, new PatchTocEntry(LuaAssetKeys.BootAssetKey, firstPath, Path.GetFileName(firstPath)));
			var second = await inspector.InspectOpaqueAsync(secondPath, new PatchTocEntry(LuaAssetKeys.BootAssetKey, secondPath, Path.GetFileName(secondPath)));

			var composition = new LuaPatchComposer().ComposeOpaque([first, second]);

			Assert.Contains("local __hd2_opaque_runner_1 = function(...)", composition.SourceText, StringComparison.Ordinal);
			Assert.Contains("local __hd2_opaque_runner_2 = function(...)", composition.SourceText, StringComparison.Ordinal);
			Assert.Contains("__hd2_opaque_runner_1(...)", composition.SourceText, StringComparison.Ordinal);
			Assert.EndsWith("return __hd2_opaque_runner_2(...)" + Environment.NewLine, composition.SourceText, StringComparison.Ordinal);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static byte[] BuildPatch(string name, string body, byte[]? boot = null)
	{
		boot ??= [27, 76, 74, 2];
		var source = $"-- {name}\n"
			+ $"local BC = \"{string.Concat(boot.Select(value => "\\" + value))}\"\n"
			+ "local loader = rawget(_G, \"loadstring\") or rawget(_G, \"load\")\n"
			+ "local chunk = loader and loader(BC)\n"
			+ "boot_ok, boot_ret = pcall(chunk)\n"
			+ body + "\n"
			+ "return boot_ret\n";
		return BuildPatchFromPayload(Encoding.UTF8.GetBytes(source), LuaResourceFormat.LegacySourceVersion);
	}

	private static byte[] BuildPatchFromPayload(byte[] payload, uint version)
	{
		var header = new byte[0xC0];
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0xF0000011);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 1);
		var typeOffset = 72;
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(typeOffset + 8, 8), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(typeOffset + 16, 8), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(typeOffset + 24, 4), 16);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(typeOffset + 28, 4), 64);
		var entryOffset = 104;
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset, 8), LuaAssetKeys.BootFileId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset + 8, 8), LuaAssetKeys.LuaTypeId);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(entryOffset + 16, 8), 0xB8);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(entryOffset + 56, 4), checked((uint)(payload.Length + 8)));
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(entryOffset + 60, 4), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(entryOffset + 64, 4), 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(entryOffset + 76, 4), 1);
		BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0xA0, 8), checked((ulong)payload.Length + 8));
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xB8, 4), checked((uint)payload.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0xBC, 4), version);
		return header.Concat(payload).ToArray();
	}
}
