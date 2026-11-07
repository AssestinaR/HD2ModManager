using System.Buffers.Binary;
using System.Text;
using HD2ModCore.Audio;

namespace HD2ModCore.Tests;

// Purpose: Verifies that outdated audio Mod banks are detected and rebuilt around the current official bank.
public sealed class AudioBankRepairServiceTests : IDisposable
{
	private const ulong BankFileId = 0x1122334455667788;
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-audio-" + Guid.NewGuid().ToString("N"));

	public AudioBankRepairServiceTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	[Fact]
	public async Task Inspect_OutdatedWeaponBank_ReportsMissingEntriesAndMedia()
	{
		var dir = WriteMod("wep", "content/audio/wep_railgun", ModBank(), mixed: false);
		var report = await Service().InspectDirectoryAsync(dir);

		var finding = Assert.Single(report.Findings);
		Assert.Equal("content/audio/wep_railgun", finding.BankName);
		Assert.Equal(1, finding.MissingEntries);
		Assert.Equal(1, finding.MissingMedia);
		Assert.True(finding.Safe);
	}

	[Fact]
	public async Task Inspect_CurrentBank_ReportsNothing()
	{
		var dir = WriteMod("current", "content/audio/wep_railgun", OfficialBank(), mixed: false);
		var report = await Service().InspectDirectoryAsync(dir);
		Assert.Empty(report.Findings);
	}

	[Fact]
	public async Task Repair_RebuildsOfficialHierarchyKeepsModMediaAndBacksUp()
	{
		var dir = WriteMod("fix", "content/audio/wep_railgun", ModBank(), mixed: false);
		var patchPath = Path.Combine(dir, "9ba626afa44a3aa3.patch_0");
		var original = await File.ReadAllBytesAsync(patchPath);
		var backup = Path.Combine(_root, "backup");

		var result = await Service().RepairDirectoryAsync(dir, backup);

		Assert.Single(result.Repaired);
		Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(backup, "9ba626afa44a3aa3.patch_0")));
		var package = PatchPackage.Parse(await File.ReadAllBytesAsync(patchPath));
		var bankEntry = Assert.Single(package.Entries, entry => entry.TypeId == PatchPackage.BankTypeId);
		var chunks = WwiseBank.ParseChunks(package.TocData(bankEntry)[16..]);
		var hirc = WwiseBank.ParseHirc(WwiseBank.Find(chunks, "HIRC")!.Data);
		var media = WwiseBank.ExtractMedia(chunks);
		Assert.Equal(new uint[] { 10, 20, 30 }, hirc.Keys.OrderBy(id => id));
		Assert.Equal(new uint[] { 1, 2, 3 }, media.Keys.OrderBy(id => id));
		Assert.Equal(ModPayload(1), media[1]);        // Mod-replaced audio is kept
		Assert.Equal(OfficialPayload(3), media[3]);   // Media the Mod never had comes from the official bank
		Assert.Empty((await Service().InspectDirectoryAsync(dir)).Findings);
	}

	[Fact]
	public async Task Repair_PatchWithNonAudioResources_IsSkippedUntouched()
	{
		var dir = WriteMod("mixed", "content/audio/wep_railgun", ModBank(), mixed: true);
		var patchPath = Path.Combine(dir, "9ba626afa44a3aa3.patch_0");
		var original = await File.ReadAllBytesAsync(patchPath);

		var result = await Service().RepairDirectoryAsync(dir, Path.Combine(_root, "backup"));

		Assert.Empty(result.Repaired);
		Assert.Single(result.Skipped);
		Assert.Equal(original, await File.ReadAllBytesAsync(patchPath));
	}

	[Fact]
	public async Task Repair_VoiceBankWithCustomEntries_IsNotRepairedByDefault()
	{
		var dir = WriteMod("voice", "content/audio/jp/Radio_Eagle_Pilot_VO", ModBankWithCustomEntries(100), mixed: false);
		var patchPath = Path.Combine(dir, "9ba626afa44a3aa3.patch_0");
		var original = await File.ReadAllBytesAsync(patchPath);

		var result = await Service().RepairDirectoryAsync(dir, Path.Combine(_root, "backup"));

		Assert.Empty(result.Repaired);
		Assert.Single(result.SkippedUnsafe);
		Assert.Equal(original, await File.ReadAllBytesAsync(patchPath));
	}

	private AudioBankRepairService Service() => new(new FakeOfficialBanks(OfficialBank()));

	private string WriteMod(string name, string bankName, byte[] bank, bool mixed)
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"), BuildPatch(bankName, bank, mixed));
		return dir;
	}

	private sealed class FakeOfficialBanks(byte[] bank) : IOfficialBankSource
	{
		public ValueTask<byte[]?> ReadBankAsync(ulong bankFileId, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<byte[]?>(bankFileId == BankFileId ? bank : null);
	}

	private static byte[] OfficialPayload(uint id) => Enumerable.Repeat((byte)(0xA0 + id), 24).ToArray();
	private static byte[] ModPayload(uint id) => Enumerable.Repeat((byte)(0x10 + id), 40).ToArray();

	// Official bank: sounds 10/20/30 with media 1/2/3.
	private static byte[] OfficialBank() => BuildBank([10, 20, 30], new() { [1] = OfficialPayload(1), [2] = OfficialPayload(2), [3] = OfficialPayload(3) });

	// Outdated Mod bank: sounds 10/20 only, media 1 replaced by the Mod and 2 untouched.
	private static byte[] ModBank() => BuildBank([10, 20], new() { [1] = ModPayload(1), [2] = OfficialPayload(2) });

	private static byte[] ModBankWithCustomEntries(int extra)
		=> BuildBank([10, 20, .. Enumerable.Range(1000, extra).Select(i => (uint)i)], new() { [1] = ModPayload(1), [2] = OfficialPayload(2) });

	private static byte[] BuildBank(uint[] soundIds, Dictionary<uint, byte[]> media)
	{
		using var hirc = new MemoryStream();
		Write32(hirc, (uint)soundIds.Length);
		foreach (var id in soundIds)
		{
			var sourceId = id / 10;
			hirc.WriteByte(2);
			Write32(hirc, 4 + 13);
			Write32(hirc, id);
			Write32(hirc, 262145);
			hirc.WriteByte(0);
			Write32(hirc, sourceId);
			Write32(hirc, 0);
		}
		using var didx = new MemoryStream();
		using var data = new MemoryStream();
		foreach (var (id, payload) in media.OrderBy(pair => pair.Key))
		{
			Write32(didx, id); Write32(didx, (uint)data.Length); Write32(didx, (uint)payload.Length);
			data.Write(payload);
			for (var i = 0; i < ((-payload.Length) & 15); i++) data.WriteByte(0);
		}
		using var bank = new MemoryStream();
		Chunk(bank, "BKHD", new byte[16]);
		Chunk(bank, "DIDX", didx.ToArray());
		Chunk(bank, "DATA", data.ToArray());
		Chunk(bank, "HIRC", hirc.ToArray());
		return bank.ToArray();
	}

	private static byte[] BuildPatch(string bankName, byte[] bank, bool mixed)
	{
		var bankResource = new byte[16 + bank.Length];
		bankResource[0] = 0xD8; bankResource[1] = 0x2F; bankResource[2] = 0x76; bankResource[3] = 0x78;
		BinaryPrimitives.WriteUInt32LittleEndian(bankResource.AsSpan(4), (uint)bank.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(bankResource.AsSpan(8), BankFileId);
		bank.CopyTo(bankResource, 16);
		var name = Encoding.UTF8.GetBytes(bankName + "\0");
		var dep = new byte[8 + name.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(dep.AsSpan(4), (uint)name.Length);
		name.CopyTo(dep, 8);

		var resources = new List<(ulong Type, byte[] Body)> { (PatchPackage.BankTypeId, bankResource), (PatchPackage.DepTypeId, dep) };
		if (mixed) resources.Add((0xDEADBEEFUL, new byte[32]));
		var types = resources.Select(resource => resource.Type).Distinct().ToArray();

		using var ms = new MemoryStream();
		Write32(ms, PatchPackage.PatchMagic);
		Write32(ms, (uint)types.Length);
		Write32(ms, (uint)resources.Count);
		Write32(ms, 0);
		ms.Write(new byte[] { 0xCE, 0x09, 0xF5, 0xF4, 0, 0, 0, 0 });
		ms.Write(new byte[48]);
		foreach (var type in types) { Write64(ms, 0); Write64(ms, type); Write64(ms, 1); Write32(ms, 16); Write32(ms, 64); }
		var offset = 72L + 32 * types.Length + 80L * resources.Count + 8;
		var offsets = new List<long>();
		foreach (var resource in resources) { offsets.Add(offset); offset += (resource.Body.Length + 15) & ~15; }
		for (var i = 0; i < resources.Count; i++)
		{
			Write64(ms, BankFileId); Write64(ms, resources[i].Type); Write64(ms, (ulong)offsets[i]); Write64(ms, 0);
			Write64(ms, 0); Write64(ms, 0); Write64(ms, 0);
			Write32(ms, (uint)resources[i].Body.Length); Write32(ms, 0); Write32(ms, 0); Write32(ms, 0); Write32(ms, 0); Write32(ms, (uint)i);
		}
		ms.Write(new byte[8]);
		foreach (var resource in resources)
		{
			ms.Write(resource.Body);
			for (var i = 0; i < (((-resource.Body.Length) & 15)); i++) ms.WriteByte(0);
		}
		while (ms.Length < 256L * resources.Count) ms.WriteByte(0);
		return ms.ToArray();
	}

	private static void Chunk(Stream stream, string tag, byte[] data)
	{
		stream.Write(Encoding.ASCII.GetBytes(tag));
		Write32(stream, (uint)data.Length);
		stream.Write(data);
	}

	private static void Write32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
	private static void Write64(Stream stream, ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); stream.Write(b); }
}
