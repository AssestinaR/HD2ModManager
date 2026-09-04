using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Guards the narrow compatibility route for game-loadable packages
// whose empty stream sidecar is paired with one repeated invalid stream-size
// sentinel written by a nonstandard packer.
public sealed class PatchStreamCompatibilityTests : IDisposable
{
	private const uint StreamSentinel = 0xdfa64c92;
	private readonly string root = Path.Combine(Path.GetTempPath(), "HD2ModAdaptationTests", Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task ScanEntriesAsync_EmptyUniformStreamSentinel_NormalizesToNoStream()
	{
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "streamless.patch_0");
		await File.WriteAllBytesAsync(path, CreatePatch(StreamSentinel));
		await File.WriteAllBytesAsync(path + ".stream", []);
		await File.WriteAllBytesAsync(path + ".gpu_resources", [9, 8, 7]);

		var entry = Assert.Single(await new PatchTocScanner().ScanEntriesAsync(path));

		Assert.Equal(0u, entry.StreamSize);
		Assert.Equal(StreamSentinel, entry.DeclaredStreamSize);
		Assert.True(entry.UsesEmptyStreamCompatibilityOverride);
		var payload = await new PatchEntryPayloadReader().ReadPayloadAsync(entry);
		Assert.Empty(payload.StreamData);
		Assert.Equal(new byte[] { 1, 2, 3 }, payload.TocData);
		Assert.Equal(new byte[] { 9, 8, 7 }, payload.GpuResourceData);
	}

	[Fact]
	public async Task ScanEntriesAsync_MissingStream_NormalizesToNoStream()
	{
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "omitted-stream.patch_0");
		await File.WriteAllBytesAsync(path, CreatePatch(StreamSentinel));

		var entry = Assert.Single(await new PatchTocScanner().ScanEntriesAsync(path));

		Assert.Equal(0u, entry.StreamSize);
		Assert.Equal(StreamSentinel, entry.DeclaredStreamSize);
		Assert.True(entry.UsesEmptyStreamCompatibilityOverride);
	}

	[Fact]
	public async Task ScanEntriesAsync_NonEmptyStream_DoesNotNormalizeSentinel()
	{
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "not-streamless.patch_0");
		await File.WriteAllBytesAsync(path, CreatePatch(StreamSentinel));
		await File.WriteAllBytesAsync(path + ".stream", [0]);

		var entry = Assert.Single(await new PatchTocScanner().ScanEntriesAsync(path));

		Assert.Equal(StreamSentinel, entry.StreamSize);
		Assert.Equal(StreamSentinel, entry.DeclaredStreamSize);
		Assert.False(entry.UsesEmptyStreamCompatibilityOverride);
	}

	public void Dispose()
	{
		if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
	}

	private static byte[] CreatePatch(uint streamSize)
	{
		const int typeOffset = 60;
		const int entryOffset = typeOffset + 32;
		const int payloadOffset = entryOffset + 80;
		var data = new byte[payloadOffset + 3];
		Write32(data, 0, 4026531857);
		Write32(data, 4, 1);
		Write32(data, 8, 1);
		Write64(data, typeOffset + 8, 0xe0a48d0be9a7453f);
		Write64(data, typeOffset + 16, 1);
		Write64(data, entryOffset, 0x1122334455667788);
		Write64(data, entryOffset + 8, 0xe0a48d0be9a7453f);
		Write64(data, entryOffset + 16, payloadOffset);
		Write32(data, entryOffset + 56, 3);
		Write32(data, entryOffset + 60, streamSize);
		Write32(data, entryOffset + 64, 3);
		Write32(data, entryOffset + 76, 1);
		data[payloadOffset] = 1;
		data[payloadOffset + 1] = 2;
		data[payloadOffset + 2] = 3;
		return data;
	}

	private static void Write32(byte[] data, int offset, uint value)
		=> BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

	private static void Write64(byte[] data, int offset, ulong value)
		=> BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), value);
}
