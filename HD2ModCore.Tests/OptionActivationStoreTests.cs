using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class OptionActivationStoreTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(4)]
	public async Task TryLoadReadOnlySnapshotAsync_RejectsUnknownVersion(int version)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, "option-activations.json");
			await File.WriteAllTextAsync(path, $$"""
			{
			  "version": {{version}},
			  "options": {}
			}
			""");

			var exception = await Assert.ThrowsAsync<InvalidDataException>(
				async () => await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path).AsTask());

			Assert.Contains("Unsupported option activation data version", exception.Message);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	public async Task TryLoadReadOnlySnapshotAsync_AcceptsKnownVersionedFormats(int version)
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, "option-activations.json");
			await File.WriteAllTextAsync(path, $$"""
			{
			  "version": {{version}},
			  "options": {}
			}
			""");

			var snapshot = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path);

			Assert.Empty(snapshot.Options);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task CleanupAsync_RemovesMultipleOptionRecordsAndHostRelationsWithOnePersistedResult()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, "option-activations.json");
			var store = new OptionActivationStore(path);

			await store.SetEnabledAsync("option-delete-a", "host-a", enabled: true);
			await store.SetEnabledAsync("option-delete-a", "host-remove-1", enabled: false);
			await store.SetEnabledAsync("option-delete-b", "host-b", enabled: true);
			await store.SetEnabledAsync("option-survives", "host-remove-1", enabled: true);
			await store.SetEnabledAsync("option-survives", "host-remove-2", enabled: false);
			await store.SetEnabledAsync("option-survives", "host-keep", enabled: true);
			await store.SetEnabledAsync("option-empties", "host-remove-1", enabled: true);
			await store.SetEnabledAsync("option-empties", "host-remove-2", enabled: false);
			await store.SetEnabledAsync("option-unaffected", "host-unaffected", enabled: true);

			var result = await store.CleanupAsync(
				["OPTION-DELETE-A", "option-delete-b", "missing-option"],
				["HOST-REMOVE-1", "host-remove-2", "missing-host"]);

			Assert.True(result.Changed);
			Assert.Equal(3, result.RemovedOptionRecordCount);
			Assert.Equal(4, result.RemovedHostRelationshipCount);

			var snapshot = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path);
			Assert.False(snapshot.Options.ContainsKey("option-delete-a"));
			Assert.False(snapshot.Options.ContainsKey("option-delete-b"));
			Assert.False(snapshot.Options.ContainsKey("option-empties"));
			Assert.True(snapshot.HasState("option-survives", "host-keep"));
			Assert.False(snapshot.HasState("option-survives", "host-remove-1"));
			Assert.False(snapshot.HasState("option-survives", "host-remove-2"));
			Assert.True(snapshot.HasState("option-unaffected", "host-unaffected"));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task CleanupAsync_WhenNothingMatches_DoesNotWrite()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, "option-activations.json");
			const string originalJson = "{\n  \"version\": 3,\n  \"options\": {\n    \"option-a\": {\n      \"host-a\": { \"enabled\": true, \"effectiveOrder\": 4 }\n    }\n  }\n}";
			await File.WriteAllTextAsync(path, originalJson);
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
			var lastWriteTimeBefore = File.GetLastWriteTimeUtc(path);
			var store = new OptionActivationStore(path);

			var result = await store.CleanupAsync(["missing-option"], ["missing-host"]);

			Assert.False(result.Changed);
			Assert.Equal(0, result.RemovedOptionRecordCount);
			Assert.Equal(0, result.RemovedHostRelationshipCount);
			Assert.Equal(originalJson, await File.ReadAllTextAsync(path));
			Assert.Equal(lastWriteTimeBefore, File.GetLastWriteTimeUtc(path));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
