using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

// Purpose: Ensures JSON-style reconstruction of collection properties does not
// make an unchanged ModNode look like changed content.
public sealed class ModNodeSemanticComparerTests
{
	[Fact]
	public void AreEqual_UsesCollectionContents_NotCollectionReferences()
	{
		var childId = ModNodeId.New();
		var node = new ModNode(
			ModNodeId.New(),
			"mod",
			new ModNodeMetadata(
				"Mod",
				"Notes",
				DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow.AddMinutes(1),
				ModNodeKind.Option,
				new[] { "host-guid" },
				"package-guid",
				"option/path",
				3,
				true),
			new[] { new PatchGroupKey("ABCDEF", 2) },
			new[] { childId },
			"fingerprint");

		var reconstructed = node with
		{
			Metadata = node.Metadata with { HostModGuids = node.Metadata.HostModGuids!.ToArray() },
			PatchGroups = node.PatchGroups.ToArray(),
			Children = node.Children.ToArray(),
		};

		Assert.NotSame(node.PatchGroups, reconstructed.PatchGroups);
		Assert.NotSame(node.Children, reconstructed.Children);
		Assert.NotSame(node.Metadata.HostModGuids, reconstructed.Metadata.HostModGuids);
		Assert.True(ModNodeSemanticComparer.AreEqual(node, reconstructed));
	}

	[Fact]
	public void AreEqual_DetectsMeaningfulChanges()
	{
		var node = new ModNode(
			ModNodeId.New(),
			"mod",
			new ModNodeMetadata("Mod", null, DateTimeOffset.UtcNow, null),
			new[] { new PatchGroupKey("ABCDEF", 0) },
			Array.Empty<ModNodeId>(),
			"fingerprint");

		Assert.False(ModNodeSemanticComparer.AreEqual(node, node with { ContentFingerprint = "changed" }));
		Assert.False(ModNodeSemanticComparer.AreEqual(node, node with { PatchGroups = new[] { new PatchGroupKey("ABCDEF", 1) } }));
		Assert.False(ModNodeSemanticComparer.AreEqual(node, node with { Metadata = node.Metadata with { Name = "Changed" } }));
	}
}
