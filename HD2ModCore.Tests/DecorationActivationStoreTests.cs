using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class DecorationActivationStoreTests
{
	[Fact]
	public async Task TryLoadReadOnlySnapshotAsync_AcceptsExistingLegacyJson()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var decorationId = Guid.NewGuid().ToString("N");
			var hostId = Guid.NewGuid().ToString("N");
			var secondHostId = Guid.NewGuid().ToString("N");
			var path = Path.Combine(root, "decoration-activations.json");
			var legacyJson = $$"""
			{
			  "{{decorationId}}": ["{{hostId.ToUpperInvariant()}}", "{{secondHostId}}"]
			}
			""";
			await File.WriteAllTextAsync(path, legacyJson);

			var snapshot = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(path);

			Assert.Single(snapshot.EnabledHostsByDecoration);
			Assert.True(snapshot.GetEnabledHosts(decorationId).SetEquals([hostId, secondHostId]));
			Assert.Equal(legacyJson, await File.ReadAllTextAsync(path));
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{\"decoration\": {}}")]
	[InlineData("{\"decoration\": [null]}")]
	[InlineData("{\"decoration\": [\"host\"]")]
	[InlineData("{\"version\": 1, \"activations\": {}}")]
	public async Task TryLoadReadOnlySnapshotAsync_RejectsUnknownOrDamagedJson(string json)
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "decoration-activations.json");
			await File.WriteAllTextAsync(path, json);

			await Assert.ThrowsAsync<InvalidDataException>(
				async () => await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(path).AsTask());
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	[Fact]
	public async Task Constructor_ToleratesDamagedJsonAsEmptyState()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "decoration-activations.json");
			await File.WriteAllTextAsync(path, "{ not-json");

			var store = new DecorationActivationStore(path);

			Assert.Empty(store.CreateSnapshot().EnabledHostsByDecoration);
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	[Fact]
	public async Task CleanupAsync_RemovesDecorationKeysAndHostReferencesInOnePersistedState()
	{
		var root = CreateTemporaryDirectory();
		try
		{
			var path = Path.Combine(root, "decoration-activations.json");
			var firstDecoration = Guid.NewGuid().ToString("N");
			var secondDecoration = Guid.NewGuid().ToString("N");
			var thirdDecoration = Guid.NewGuid().ToString("N");
			var firstHost = Guid.NewGuid().ToString("N");
			var secondHost = Guid.NewGuid().ToString("N");
			var thirdHost = Guid.NewGuid().ToString("N");
			var store = new DecorationActivationStore(path);
			await store.SetEnabledHostsBatchAsync(new Dictionary<string, IReadOnlyCollection<string>>
			{
				[firstDecoration] = [firstHost, secondHost],
				[secondDecoration] = [firstHost],
				[thirdDecoration] = [thirdHost]
			});

			var result = await store.CleanupAsync([firstDecoration], [firstHost]);
			var reloaded = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(path);

			Assert.True(result.Changed);
			Assert.Equal(2, result.RemovedDecorationCount);
			Assert.Equal(1, result.RemovedHostReferenceCount);
			Assert.False(reloaded.EnabledHostsByDecoration.ContainsKey(firstDecoration));
			Assert.False(reloaded.EnabledHostsByDecoration.ContainsKey(secondDecoration));
			Assert.True(reloaded.GetEnabledHosts(thirdDecoration).SetEquals([thirdHost]));
		}
		finally
		{
			DeleteTemporaryDirectory(root);
		}
	}

	private static string CreateTemporaryDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void DeleteTemporaryDirectory(string root)
	{
		try { Directory.Delete(root, recursive: true); }
		catch { }
	}
}
