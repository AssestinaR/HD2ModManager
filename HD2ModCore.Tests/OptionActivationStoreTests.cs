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
}
