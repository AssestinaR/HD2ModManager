using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ProfileMutationServiceTests
{
	private static readonly DateTimeOffset InitialTime = new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);

	[Fact]
	public void CreateRenameAndSetActiveProfile_UseTrimmedUniqueNamesWithoutChangingDeploymentRevision()
	{
		var clock = new TestTimeProvider(InitialTime);
		var service = new ProfileMutationService(clock);
		var existing = Profile("Existing");
		var snapshot = Snapshot(Array.Empty<ModNode>(), [existing]);

		var created = service.CreateProfile(snapshot, "  Combat Setup  ", makeActive: true);

		Assert.Equal(ProfileMutationStatus.Applied, created.Status);
		Assert.True(created.Changed);
		Assert.NotNull(created.ProfileId);
		Assert.Equal(created.ProfileId, created.Snapshot.ActiveProfileId);
		var newProfile = Assert.Single(created.Snapshot.Profiles, profile => profile.Id == created.ProfileId);
		Assert.Equal("Combat Setup", newProfile.Name);
		Assert.Equal(InitialTime, newProfile.CreatedUtc);
		Assert.Null(newProfile.ModifiedUtc);
		Assert.Equal(0, newProfile.Revision);

		clock.Advance(TimeSpan.FromMinutes(1));
		var renamed = service.RenameProfile(created.Snapshot, newProfile.Id, "  Stealth Setup ");

		Assert.Equal(ProfileMutationStatus.Applied, renamed.Status);
		var renamedProfile = Assert.Single(renamed.Snapshot.Profiles, profile => profile.Id == newProfile.Id);
		Assert.Equal("Stealth Setup", renamedProfile.Name);
		Assert.Equal(clock.GetUtcNow(), renamedProfile.ModifiedUtc);
		Assert.Equal(0, renamedProfile.Revision);

		var conflict = service.RenameProfile(renamed.Snapshot, newProfile.Id, "existing");
		Assert.Equal(ProfileMutationStatus.Rejected, conflict.Status);
		Assert.Equal(ProfileMutationFailure.ProfileNameConflict, conflict.Failure);
		Assert.Same(renamed.Snapshot, conflict.Snapshot);

		var switched = service.SetActiveProfile(renamed.Snapshot, existing.Id);
		Assert.Equal(ProfileMutationStatus.Applied, switched.Status);
		Assert.Equal(existing.Id, switched.Snapshot.ActiveProfileId);
		var cleared = service.SetActiveProfile(switched.Snapshot, null);
		Assert.Equal(ProfileMutationStatus.Applied, cleared.Status);
		Assert.Null(cleared.Snapshot.ActiveProfileId);
	}

	[Fact]
	public void CreateAndSetActiveProfile_RejectConflictsAndUnknownProfilesWithoutChangingTheSnapshot()
	{
		var service = new ProfileMutationService(new TestTimeProvider(InitialTime));
		var existing = Profile("Existing");
		var snapshot = Snapshot(Array.Empty<ModNode>(), [existing]);

		var duplicate = service.CreateProfile(snapshot, " existing ");
		Assert.Equal(ProfileMutationStatus.Rejected, duplicate.Status);
		Assert.Equal(ProfileMutationFailure.ProfileNameConflict, duplicate.Failure);
		Assert.Same(snapshot, duplicate.Snapshot);

		var empty = service.CreateProfile(snapshot, "   ");
		Assert.Equal(ProfileMutationFailure.InvalidProfileName, empty.Failure);
		Assert.Same(snapshot, empty.Snapshot);

		var unknown = service.SetActiveProfile(snapshot, ProfileId.New());
		Assert.Equal(ProfileMutationFailure.ProfileNotFound, unknown.Failure);
		Assert.Same(snapshot, unknown.Snapshot);
	}

	[Fact]
	public void DeleteProfile_RemovesOnlyRequestedProfile_AndClearsActiveSelectionWhenNeeded()
	{
		var service = new ProfileMutationService(new TestTimeProvider(InitialTime));
		var active = Profile("Active");
		var retained = Profile("Retained");
		var snapshot = Snapshot(Array.Empty<ModNode>(), [active, retained]) with { ActiveProfileId = active.Id };

		var deleted = service.DeleteProfile(snapshot, active.Id);

		Assert.Equal(ProfileMutationStatus.Applied, deleted.Status);
		Assert.Empty(deleted.Snapshot.Profiles.Where(profile => profile.Id == active.Id));
		Assert.Single(deleted.Snapshot.Profiles, profile => profile.Id == retained.Id);
		Assert.Null(deleted.Snapshot.ActiveProfileId);

		var missing = service.DeleteProfile(deleted.Snapshot, active.Id);
		Assert.Equal(ProfileMutationStatus.Rejected, missing.Status);
		Assert.Equal(ProfileMutationFailure.ProfileNotFound, missing.Failure);
		Assert.Same(deleted.Snapshot, missing.Snapshot);
	}

	[Fact]
	public void AddAndRemoveEntries_AcceptsEmptyStandardEntries_AndRejectsDuplicatesOptionsAndDecorations()
	{
		var service = new ProfileMutationService(new TestTimeProvider(InitialTime));
		var standard = Node("Standard", ModNodeKind.Standard, hasPatch: true);
		var reshade = Node("ReShade", ModNodeKind.ReShade, hasPatch: false);
		var option = Node("Option", ModNodeKind.Option, hasPatch: true);
		var decoration = Node("Decoration", ModNodeKind.Decoration, hasPatch: true);
		var emptyStandard = Node("Placeholder", ModNodeKind.Standard, hasPatch: false);
		var profile = Profile("Profile");
		var snapshot = Snapshot([standard, reshade, option, decoration, emptyStandard], [profile]);

		var added = service.AddEntries(snapshot, profile.Id, [standard.Id, reshade.Id, emptyStandard.Id]);
		Assert.Equal(ProfileMutationStatus.Applied, added.Status);
		Assert.Equal([standard.Id, reshade.Id, emptyStandard.Id], Entries(added.Snapshot, profile.Id));
		Assert.Equal(1, FindProfile(added.Snapshot, profile.Id).Revision);

		var removed = service.RemoveEntries(added.Snapshot, profile.Id, [standard.Id]);
		Assert.Equal(ProfileMutationStatus.Applied, removed.Status);
		Assert.Equal([reshade.Id, emptyStandard.Id], Entries(removed.Snapshot, profile.Id));
		Assert.Equal(2, FindProfile(removed.Snapshot, profile.Id).Revision);

		AssertRejected(service.AddEntries(snapshot, profile.Id, [standard.Id, standard.Id]), ProfileMutationFailure.DuplicateEntrySelection, snapshot);
		AssertRejected(service.AddEntries(added.Snapshot, profile.Id, [standard.Id]), ProfileMutationFailure.EntryAlreadyPresent, added.Snapshot);
		AssertRejected(service.AddEntries(snapshot, profile.Id, [option.Id]), ProfileMutationFailure.NodeKindNotSupported, snapshot);
		AssertRejected(service.AddEntries(snapshot, profile.Id, [decoration.Id]), ProfileMutationFailure.NodeKindNotSupported, snapshot);
		AssertRejected(service.RemoveEntries(added.Snapshot, profile.Id, [option.Id]), ProfileMutationFailure.NodeKindNotSupported, added.Snapshot);
	}

	[Fact]
	public void InsertMoveAndReorderEntries_AreStableAndNormalizeLoadOrder()
	{
		var clock = new TestTimeProvider(InitialTime);
		var service = new ProfileMutationService(clock);
		var a = Node("A");
		var b = Node("B");
		var c = Node("C");
		var d = Node("D");
		var e = Node("E");
		var f = Node("F");
		var profile = Profile(
			"Profile",
			[
				new ProfileEntry(a.Id, 0, InitialTime.AddSeconds(1)),
				new ProfileEntry(b.Id, 1, InitialTime.AddSeconds(2)),
				new ProfileEntry(c.Id, 2, InitialTime.AddSeconds(3)),
				new ProfileEntry(d.Id, 3, InitialTime.AddSeconds(4)),
			]);
		var snapshot = Snapshot([a, b, c, d, e, f], [profile]);

		var moved = service.MoveEntries(snapshot, profile.Id, [d.Id, b.Id], targetPosition: 0);
		Assert.Equal([b.Id, d.Id, a.Id, c.Id], Entries(moved.Snapshot, profile.Id));
		Assert.Equal([0, 1, 2, 3], LoadOrders(moved.Snapshot, profile.Id));

		clock.Advance(TimeSpan.FromMinutes(1));
		var inserted = service.InsertEntries(moved.Snapshot, profile.Id, [e.Id, f.Id], targetPosition: 2);
		Assert.Equal([b.Id, d.Id, e.Id, f.Id, a.Id, c.Id], Entries(inserted.Snapshot, profile.Id));
		Assert.Equal(clock.GetUtcNow(), FindProfile(inserted.Snapshot, profile.Id).Entries.Single(entry => entry.NodeId == e.Id).AddedUtc);
		Assert.Equal(clock.GetUtcNow(), FindProfile(inserted.Snapshot, profile.Id).Entries.Single(entry => entry.NodeId == f.Id).AddedUtc);

		var reordered = service.ReorderEntries(inserted.Snapshot, profile.Id, [c.Id, a.Id, f.Id, e.Id, d.Id, b.Id]);
		Assert.Equal([c.Id, a.Id, f.Id, e.Id, d.Id, b.Id], Entries(reordered.Snapshot, profile.Id));
		Assert.Equal([0, 1, 2, 3, 4, 5], LoadOrders(reordered.Snapshot, profile.Id));
		Assert.Equal(3, FindProfile(reordered.Snapshot, profile.Id).Revision);
	}

	[Fact]
	public void MoveAndReorderEntries_RejectInvalidPositionsAndIncompleteOrDuplicateOrdersWithoutMutation()
	{
		var service = new ProfileMutationService(new TestTimeProvider(InitialTime));
		var a = Node("A");
		var b = Node("B");
		var c = Node("C");
		var profile = Profile("Profile", [new ProfileEntry(a.Id, 0), new ProfileEntry(b.Id, 1), new ProfileEntry(c.Id, 2)]);
		var snapshot = Snapshot([a, b, c], [profile]);

		AssertRejected(service.MoveEntries(snapshot, profile.Id, [a.Id], targetPosition: 4), ProfileMutationFailure.InvalidTargetPosition, snapshot);
		AssertRejected(service.ReorderEntries(snapshot, profile.Id, [a.Id, b.Id]), ProfileMutationFailure.ReorderMustContainEveryEntry, snapshot);
		AssertRejected(service.ReorderEntries(snapshot, profile.Id, [a.Id, a.Id, b.Id]), ProfileMutationFailure.DuplicateEntrySelection, snapshot);
		AssertRejected(service.MoveEntries(snapshot, profile.Id, [a.Id, c.Id], targetPosition: 5), ProfileMutationFailure.InvalidTargetPosition, snapshot);
	}

	[Fact]
	public void ReplaceEntries_ReplacesCompleteMembershipOnce_PreservesExistingAddedUtc_AndAllowsEmpty()
	{
		var clock = new TestTimeProvider(InitialTime);
		var service = new ProfileMutationService(clock);
		var a = Node("A");
		var b = Node("B");
		var c = Node("C");
		var option = Node("Option", ModNodeKind.Option, hasPatch: true);
		var profile = Profile(
			"Profile",
			[
				new ProfileEntry(a.Id, 0, InitialTime.AddSeconds(1)),
				new ProfileEntry(b.Id, 1, InitialTime.AddSeconds(2)),
			]);
		var snapshot = Snapshot([a, b, c, option], [profile]);

		clock.Advance(TimeSpan.FromMinutes(2));
		var replaced = service.ReplaceEntries(snapshot, profile.Id, [c.Id, a.Id]);
		Assert.Equal(ProfileMutationStatus.Applied, replaced.Status);
		Assert.Equal([c.Id, a.Id], Entries(replaced.Snapshot, profile.Id));
		var changed = FindProfile(replaced.Snapshot, profile.Id);
		Assert.Equal(1, changed.Revision);
		Assert.Equal(InitialTime.AddSeconds(1), changed.Entries.Single(entry => entry.NodeId == a.Id).AddedUtc);
		Assert.Equal(clock.GetUtcNow(), changed.Entries.Single(entry => entry.NodeId == c.Id).AddedUtc);

		var same = service.ReplaceEntries(replaced.Snapshot, profile.Id, [c.Id, a.Id]);
		Assert.Equal(ProfileMutationStatus.NoChange, same.Status);
		Assert.Same(replaced.Snapshot, same.Snapshot);

		var emptied = service.ReplaceEntries(replaced.Snapshot, profile.Id, Array.Empty<ModNodeId>());
		Assert.Equal(ProfileMutationStatus.Applied, emptied.Status);
		Assert.Empty(Entries(emptied.Snapshot, profile.Id));
		Assert.Equal(2, FindProfile(emptied.Snapshot, profile.Id).Revision);

		AssertRejected(service.ReplaceEntries(snapshot, profile.Id, [a.Id, a.Id]), ProfileMutationFailure.DuplicateEntrySelection, snapshot);
		AssertRejected(service.ReplaceEntries(snapshot, profile.Id, [option.Id]), ProfileMutationFailure.NodeKindNotSupported, snapshot);
	}

	[Fact]
	public void EntryMutations_RejectUnknownProfilesAndMalformedExistingProfileEntries()
	{
		var service = new ProfileMutationService(new TestTimeProvider(InitialTime));
		var standard = Node("Standard");
		var option = Node("Option", ModNodeKind.Option, hasPatch: true);
		var valid = Profile("Valid");
		var malformed = Profile("Malformed", [new ProfileEntry(option.Id, 0)]);
		var snapshot = Snapshot([standard, option], [valid, malformed]);

		AssertRejected(service.AddEntries(snapshot, ProfileId.New(), [standard.Id]), ProfileMutationFailure.ProfileNotFound, snapshot);
		AssertRejected(service.AddEntries(snapshot, malformed.Id, [standard.Id]), ProfileMutationFailure.ProfileContainsNonDeployableEntry, snapshot);
		AssertRejected(service.SetActiveProfile(snapshot, malformed.Id), ProfileMutationFailure.ProfileContainsNonDeployableEntry, snapshot);

		var duplicateProfile = Profile("Duplicate", [new ProfileEntry(standard.Id, 0), new ProfileEntry(standard.Id, 1)]);
		var duplicateSnapshot = Snapshot([standard], [duplicateProfile]);
		AssertRejected(service.RemoveEntries(duplicateSnapshot, duplicateProfile.Id, [standard.Id]), ProfileMutationFailure.ProfileContainsDuplicateEntries, duplicateSnapshot);
	}

	private static void AssertRejected(ProfileMutationResult result, ProfileMutationFailure expectedFailure, LibrarySnapshot expectedSnapshot)
	{
		Assert.Equal(ProfileMutationStatus.Rejected, result.Status);
		Assert.Equal(expectedFailure, result.Failure);
		Assert.Same(expectedSnapshot, result.Snapshot);
		Assert.False(result.Changed);
	}

	private static IReadOnlyList<ModNodeId> Entries(LibrarySnapshot snapshot, ProfileId profileId)
		=> FindProfile(snapshot, profileId).Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray();

	private static IReadOnlyList<int> LoadOrders(LibrarySnapshot snapshot, ProfileId profileId)
		=> FindProfile(snapshot, profileId).Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.LoadOrder).ToArray();

	private static Profile FindProfile(LibrarySnapshot snapshot, ProfileId profileId)
		=> Assert.Single(snapshot.Profiles, profile => profile.Id == profileId);

	private static LibrarySnapshot Snapshot(IReadOnlyList<ModNode> nodes, IReadOnlyList<Profile> profiles)
		=> new(1, InitialTime, nodes.ToDictionary(node => node.Id), profiles);

	private static Profile Profile(string name, IReadOnlyList<ProfileEntry>? entries = null)
		=> new(ProfileId.New(), name, InitialTime, null, entries ?? Array.Empty<ProfileEntry>());

	private static ModNode Node(string name, ModNodeKind kind = ModNodeKind.Standard, bool hasPatch = true)
		=> new(
			ModNodeId.New(),
			name,
			new ModNodeMetadata(name, null, InitialTime, null, kind),
			hasPatch ? [new PatchGroupKey("9ba626afa44a3aa3", 0)] : Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

	private sealed class TestTimeProvider : TimeProvider
	{
		private DateTimeOffset _utcNow;

		public TestTimeProvider(DateTimeOffset utcNow)
		{
			_utcNow = utcNow;
		}

		public override DateTimeOffset GetUtcNow() => _utcNow;

		public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
	}
}
