using HD2ModCore.Domain;

namespace HD2ModCore.Audio;

// Purpose: Detects and repairs audio Mod patches whose Wwise banks are older than the current official bank.
// A Mod ships a whole copy of a bank; after a game update that copy lacks new events/media, so the game loses the
// original sounds too. The repair keeps the Mod's replaced media but rebuilds the bank around the official hierarchy.

/// <summary>Reads current official bank resources (without the 16-byte resource header) by file id.</summary>
public interface IOfficialBankSource
{
	ValueTask<byte[]?> ReadBankAsync(ulong bankFileId, CancellationToken cancellationToken = default);
}

public sealed record AudioBankFinding(
	string PatchPath,
	ulong BankFileId,
	string BankName,
	int MissingEntries,
	int MissingMedia,
	int OnlyModEntries,
	int OnlyModMedia,
	bool Safe);

public sealed record AudioPatchSkip(string PatchPath, string Reason);

public sealed record AudioDirectoryInspection(
	IReadOnlyList<AudioBankFinding> Findings,
	IReadOnlyList<AudioPatchSkip> Skipped)
{
	public bool IsOutdated => Findings.Count > 0;
	public bool HasRepairable => Findings.Any(finding => finding.Safe);
}

public sealed record AudioRepairedPatch(string PatchPath, IReadOnlyList<string> Notes);

public sealed record AudioDirectoryRepairResult(
	IReadOnlyList<AudioRepairedPatch> Repaired,
	IReadOnlyList<AudioBankFinding> SkippedUnsafe,
	IReadOnlyList<AudioPatchSkip> Skipped);

public sealed class AudioBankRepairService(IOfficialBankSource officialBanks)
{
	private static readonly string[] WeaponishKeys = ["weapon", "/wep_", "pistol", "rifle", "shotgun", "launcher", "melee", "foley", "gun", "bomb", "cannon"];

	private readonly IOfficialBankSource _officialBanks = officialBanks ?? throw new ArgumentNullException(nameof(officialBanks));

	public async ValueTask<AudioDirectoryInspection> InspectDirectoryAsync(string directory, CancellationToken cancellationToken = default)
	{
		var findings = new List<AudioBankFinding>();
		var skipped = new List<AudioPatchSkip>();
		foreach (var patchPath in EnumeratePatches(directory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var plans = await EvaluatePatchAsync(patchPath, cancellationToken).ConfigureAwait(false);
			skipped.AddRange(plans.Skipped);
			findings.AddRange(plans.Plans.Select(plan => plan.Finding));
		}
		return new AudioDirectoryInspection(findings, skipped);
	}

	/// <summary>
	/// Rebuilds every safe outdated bank in the directory and writes the result next to the original.
	/// Originals (patch and .stream) are copied to <paramref name="backupDirectory"/> first; on any write failure the
	/// originals of that patch are restored and the exception is rethrown.
	/// </summary>
	public async ValueTask<AudioDirectoryRepairResult> RepairDirectoryAsync(
		string directory,
		string backupDirectory,
		bool includeUnsafe = false,
		CancellationToken cancellationToken = default)
	{
		var repaired = new List<AudioRepairedPatch>();
		var unsafeFindings = new List<AudioBankFinding>();
		var skipped = new List<AudioPatchSkip>();
		foreach (var patchPath in EnumeratePatches(directory))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var evaluation = await EvaluatePatchAsync(patchPath, cancellationToken).ConfigureAwait(false);
			skipped.AddRange(evaluation.Skipped);
			var selected = evaluation.Plans.Where(plan => includeUnsafe || plan.Finding.Safe).ToArray();
			unsafeFindings.AddRange(evaluation.Plans.Where(plan => !selected.Contains(plan)).Select(plan => plan.Finding));
			if (selected.Length == 0) continue;

			var notes = new List<string>();
			var newBanks = new Dictionary<ulong, byte[]>();
			foreach (var plan in selected)
				newBanks[plan.Finding.BankFileId] = BuildBank(plan, notes);

			var streamPath = patchPath + ".stream";
			var originalStream = File.Exists(streamPath) ? await File.ReadAllBytesAsync(streamPath, cancellationToken).ConfigureAwait(false) : [];
			var newPatch = evaluation.Package!.Rebuild(newBanks, originalStream, out var newStream);

			BackupAndWrite(patchPath, newPatch, newStream, backupDirectory);
			repaired.Add(new AudioRepairedPatch(patchPath, notes));
		}
		return new AudioDirectoryRepairResult(repaired, unsafeFindings, skipped);
	}

	private static IEnumerable<string> EnumeratePatches(string directory)
	{
		if (!Directory.Exists(directory)) return [];
		return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => IsPatchFileName(Path.GetFileName(path)))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
	}

	// <16 hex>.patch_<n> only; sidecars (.stream/.gpu_resources/.bak) are never TOC files.
	internal static bool IsPatchFileName(string name)
	{
		var marker = name.IndexOf(".patch_", StringComparison.OrdinalIgnoreCase);
		if (marker != 16) return false;
		var suffix = name[(marker + 7)..];
		return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
	}

	private sealed record BankPlan(
		AudioBankFinding Finding,
		byte[] OfficialBank,
		Dictionary<uint, byte[]> OfficialMedia,
		Dictionary<uint, byte[]> ModMedia);

	private sealed record PatchEvaluation(PatchPackage? Package, IReadOnlyList<BankPlan> Plans, IReadOnlyList<AudioPatchSkip> Skipped);

	private async ValueTask<PatchEvaluation> EvaluatePatchAsync(string patchPath, CancellationToken cancellationToken)
	{
		PatchPackage package;
		try
		{
			package = PatchPackage.Parse(await File.ReadAllBytesAsync(patchPath, cancellationToken).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
		{
			return new PatchEvaluation(null, [], [new AudioPatchSkip(patchPath, "解析失败：" + exception.Message)]);
		}

		var banks = package.Entries.Where(entry => entry.TypeId == PatchPackage.BankTypeId).ToArray();
		if (banks.Length == 0) return new PatchEvaluation(package, [], []);
		if (!package.IsAudioOnly)
			return new PatchEvaluation(package, [], [new AudioPatchSkip(patchPath, "含贴图/单位等非音频资源，为避免绿屏/崩溃已跳过")]);

		var plans = new List<BankPlan>();
		foreach (var entry in banks)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = package.DepName(entry.FileId) ?? string.Empty;
			if (name.EndsWith("/Init", StringComparison.OrdinalIgnoreCase)
				|| name.EndsWith("/game_init", StringComparison.OrdinalIgnoreCase)) continue;

			var modResource = package.TocData(entry);
			if (modResource.Length < 16) continue;
			var official = await _officialBanks.ReadBankAsync(entry.FileId, cancellationToken).ConfigureAwait(false);
			if (official is null) continue;

			IReadOnlyList<WwiseBankChunk> officialChunks, modChunks;
			Dictionary<uint, WwiseHircEntry> officialHirc, modHirc;
			try
			{
				officialChunks = WwiseBank.ParseChunks(official);
				modChunks = WwiseBank.ParseChunks(modResource[16..]);
				var oh = WwiseBank.Find(officialChunks, "HIRC");
				var mh = WwiseBank.Find(modChunks, "HIRC");
				if (oh is null || mh is null) continue;
				officialHirc = WwiseBank.ParseHirc(oh.Data);
				modHirc = WwiseBank.ParseHirc(mh.Data);
			}
			catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException)
			{
				continue;
			}

			var officialMedia = WwiseBank.ExtractMedia(officialChunks);
			var modMedia = WwiseBank.ExtractMedia(modChunks);
			var missingEntries = officialHirc.Keys.Count(id => !modHirc.ContainsKey(id));
			var missingMedia = officialMedia.Keys.Count(id => !modMedia.ContainsKey(id));
			if (missingEntries == 0 && missingMedia == 0) continue;

			var onlyModEntries = modHirc.Keys.Count(id => !officialHirc.ContainsKey(id));
			var onlyModMedia = modMedia.Keys.Count(id => !officialMedia.ContainsKey(id));
			var ratioSafe = onlyModEntries <= Math.Max(20, officialHirc.Count / 20)
				&& onlyModMedia <= Math.Max(10, officialMedia.Count / 10);
			var safe = ratioSafe && (onlyModEntries == 0 || IsWeaponish(name));
			plans.Add(new BankPlan(
				new AudioBankFinding(patchPath, entry.FileId, name, missingEntries, missingMedia, onlyModEntries, onlyModMedia, safe),
				official, officialMedia, modMedia));
		}
		return new PatchEvaluation(package, plans, []);
	}

	private static bool IsWeaponish(string bankName)
	{
		if (string.IsNullOrEmpty(bankName)) return false;
		var lower = bankName.ToLowerInvariant();
		return WeaponishKeys.Any(key => lower.Contains(key, StringComparison.Ordinal));
	}

	// Official media is the base; only media the Mod actually changed overrides it. If the merged bank does not
	// verify, fall back to the untouched official bank so vanilla sound is guaranteed.
	private static byte[] BuildBank(BankPlan plan, List<string> notes)
	{
		var merged = new Dictionary<uint, byte[]>(plan.OfficialMedia);
		foreach (var (id, payload) in plan.ModMedia)
		{
			if (merged.TryGetValue(id, out var current) && !current.AsSpan().SequenceEqual(payload))
				merged[id] = payload;
		}
		try
		{
			var bank = WwiseBank.Build(plan.OfficialBank, merged);
			WwiseBank.Verify(bank, merged, plan.OfficialMedia.Keys.ToHashSet());
			return bank;
		}
		catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException)
		{
			notes.Add($"{plan.Finding.BankName} -> 已退回官方音库（Mod 音色失效）：{exception.Message}");
			return plan.OfficialBank;
		}
	}

	private static void BackupAndWrite(string patchPath, byte[] patch, byte[] stream, string backupDirectory)
	{
		Directory.CreateDirectory(backupDirectory);
		var streamPath = patchPath + ".stream";
		var patchBackup = Path.Combine(backupDirectory, Path.GetFileName(patchPath));
		var streamBackup = Path.Combine(backupDirectory, Path.GetFileName(streamPath));
		File.Copy(patchPath, patchBackup, overwrite: false);
		var hadStream = File.Exists(streamPath);
		if (hadStream) File.Copy(streamPath, streamBackup, overwrite: false);
		try
		{
			ReplaceFile(patchPath, patch);
			if (stream.Length > 0) ReplaceFile(streamPath, stream);
		}
		catch
		{
			// Restore this patch's originals; the exception then reaches the caller.
			File.Copy(patchBackup, patchPath, overwrite: true);
			if (hadStream) File.Copy(streamBackup, streamPath, overwrite: true);
			throw;
		}
	}

	// Replace by rename (not in-place write) so a hard link to the deployed copy is never modified behind the manager's back.
	private static void ReplaceFile(string path, byte[] data)
	{
		var temporary = path + ".hd2audio.tmp";
		try
		{
			File.WriteAllBytes(temporary, data);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}
}
