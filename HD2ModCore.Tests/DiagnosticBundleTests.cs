using System.IO.Compression;
using System.Text;
using HD2ModCore.Infrastructure.Diagnostics;

namespace HD2ModCore.Tests;

public sealed class DiagnosticBundleTests : IDisposable
{
	private readonly string root = Directory.CreateTempSubdirectory("hd2-diagnostics-tests-").FullName;

	[Fact]
	public async Task ArchiveContainsCompleteEntriesAndLeavesExistingTargetOnConflict()
	{
		var path = Path.Combine(root, "diagnostic.zip");
		var entries = new Dictionary<string, byte[]>
		{
			["summary.txt"] = Encoding.UTF8.GetBytes("故障诊断"),
			["manifest.json"] = "{}"u8.ToArray(),
		};
		await DiagnosticBundleWriter.WriteAsync(path, entries, CancellationToken.None);
		using (var archive = ZipFile.OpenRead(path))
		{
			Assert.Equal(entries.Keys, archive.Entries.Select(entry => entry.FullName));
			using var reader = new StreamReader(archive.GetEntry("summary.txt")!.Open());
			Assert.Equal("故障诊断", await reader.ReadToEndAsync());
		}
		var original = await File.ReadAllBytesAsync(path);
		await Assert.ThrowsAsync<IOException>(() => DiagnosticBundleWriter.WriteAsync(path, entries, CancellationToken.None));
		Assert.Equal(original, await File.ReadAllBytesAsync(path));
		Assert.Single(Directory.EnumerateFiles(root));
	}

	[Fact]
	public async Task CanceledOrInvalidExportDoesNotLeaveArtifacts()
	{
		var path = Path.Combine(root, "diagnostic.zip");
		var entries = new Dictionary<string, byte[]> { ["summary.txt"] = [] };
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticBundleWriter.WriteAsync(path, entries, new CancellationToken(true)));
		entries["../settings.json"] = [];
		await Assert.ThrowsAsync<InvalidDataException>(() => DiagnosticBundleWriter.WriteAsync(path, entries, CancellationToken.None));
		Assert.Empty(Directory.EnumerateFiles(root));
	}

	[Fact]
	public async Task JournalRestartsPreserveOperationIdentityAndDiscardUntrustedText()
	{
		var path = Path.Combine(root, "events.jsonl");
		var original = new DiagnosticJournal(path);
		var operation = original.Record(DiagnosticAction.Deploy, DiagnosticResult.Started, DiagnosticTrigger.User, revision: 42);
		original.Record(DiagnosticAction.Deploy, DiagnosticResult.Failed, operation: operation, code: "PatchWriteFailed");
		original.Record(DiagnosticAction.ManagerException, DiagnosticResult.Failed, code: @"C:\Users\private\token=sentinel");
		await original.FlushAsync(CancellationToken.None);
		var reader = new DiagnosticJournal(path);
		var history = await reader.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
		Assert.False(history.StorageUnavailable);
		Assert.Equal(3, history.Events.Count);
		Assert.Equal(operation, history.Events[0].Operation);
		Assert.Equal(operation, history.Events[1].Operation);
		Assert.Equal("PatchWriteFailed", history.Events[1].Code);
		Assert.Null(history.Events[2].Code);
		Assert.DoesNotContain("sentinel", await File.ReadAllTextAsync(path));
	}

	[Fact]
	public async Task JournalDoesNotLoseOtherSessionAndRetainsOnlyNewestEvents()
	{
		var path = Path.Combine(root, "events.jsonl");
		var first = new DiagnosticJournal(path);
		var second = new DiagnosticJournal(path);
		for (var index = 0; index < DiagnosticBundleWriter.MaxEvents + 30; index++)
			(index % 2 == 0 ? first : second).Record(DiagnosticAction.ChangeSettings, DiagnosticResult.Completed, count: index);
		await Task.WhenAll(first.FlushAsync(CancellationToken.None), second.FlushAsync(CancellationToken.None));
		var history = await new DiagnosticJournal(path).ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
		Assert.False(history.StorageUnavailable);
		Assert.Equal(DiagnosticBundleWriter.MaxEvents, history.Events.Count);
		Assert.DoesNotContain(history.Events, item => item.Count < 30);
		Assert.Equal(2, history.Events.Select(item => item.Session).Distinct().Count());
		Assert.True(new FileInfo(path).Length < DiagnosticBundleWriter.MaxSourceBytes);
	}

	[Fact]
	public async Task UnavailableJournalRetainsInMemoryEvidence()
	{
		var path = Path.Combine(root, "events.jsonl");
		Directory.CreateDirectory(path);
		var journal = new DiagnosticJournal(path);
		journal.Record(DiagnosticAction.Import, DiagnosticResult.Failed, code: "NoImportableMod");
		await journal.FlushAsync(CancellationToken.None);
		var history = await journal.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
		Assert.True(history.StorageUnavailable);
		Assert.Equal("NoImportableMod", Assert.Single(history.Events).Code);
		Assert.Empty(Directory.EnumerateFiles(root));
	}

	[Fact]
	public async Task TailSkipsCutUtf8AndIncompleteRecords()
	{
		var path = Path.Combine(root, "timeline.log");
		await File.WriteAllTextAsync(path, "很长的一行\n完整\n没有写完");
		var (text, truncated) = await DiagnosticFiles.ReadTextAsync(path, 23, tail: true, CancellationToken.None);
		Assert.True(truncated);
		Assert.Equal("完整\n", text);
	}

	[Fact]
	public void LegacyLogsProjectDiagnosticFieldsWithoutCopyingMessagesOrPaths()
	{
		var now = DateTimeOffset.UtcNow;
		var manager = $"[{now:O}] ERROR Unhandled UI exception: System.IO.IOException: https://server/?token=secret\n"
			+ @"   at HD2ModManager.Sample.Run() in C:\Users\secret\Sample.cs:line 20" + "\n"
			+ $"[{now:O}] INFO unrecognized user text secret\n";
		var record = Assert.Single(DiagnosticLogParser.Manager(manager, now.AddHours(-1), now.AddHours(1), out int omitted));
		Assert.Equal("System.IO.IOException", record.Exception);
		Assert.Equal("HD2ModManager.Sample.Run", Assert.Single(record.Frames!));
		Assert.Equal(1, omitted);
		Assert.DoesNotContain("secret", Encoding.UTF8.GetString(DiagnosticBundleWriter.Serialize(record)));

		var lua = Assert.Single(DiagnosticLogParser.Lua(@"apply_error: revision=42 C:\Users\secret\script.lua:75: attempt to index a nil value", out _));
		Assert.Equal("ApplyError", lua.Code);
		Assert.Equal("NilValue", lua.DetailCode);
		Assert.Equal(42, lua.Revision);
		Assert.Equal(75, lua.LineNumber);
		Assert.Null(lua.AtUtc);
		Assert.DoesNotContain("secret", Encoding.UTF8.GetString(DiagnosticBundleWriter.Serialize(lua)));

		var kitHider = Assert.Single(DiagnosticLogParser.Lua("{\"kind\":\"rules_error\",\"reason\":\"secret\",\"t\":12.5}", out _));
		Assert.Equal("RulesError", kitHider.Code);
		Assert.Equal(12.5, kitHider.RuntimeSeconds);
		Assert.DoesNotContain("secret", Encoding.UTF8.GetString(DiagnosticBundleWriter.Serialize(kitHider)));
	}

	public void Dispose() => Directory.Delete(root, recursive: true);
}
