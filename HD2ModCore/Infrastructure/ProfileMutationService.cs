using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Pure, UI-neutral Profile mutation rules. This class never reads or writes a
/// store; callers supply the snapshot to transform and persist the returned
/// snapshot only after their own validation and write-lock policy succeeds.
/// Profile membership is an intent/reference layer: a Standard node without
/// PatchGroups is valid here and is filtered only by deployment planning.
/// </summary>
public sealed class ProfileMutationService : IProfileMutationService
{
	private readonly TimeProvider _timeProvider;

	public ProfileMutationService(TimeProvider? timeProvider = null)
	{
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public ProfileMutationResult CreateProfile(LibrarySnapshot snapshot, string name, bool makeActive = false)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!TryNormalizeName(name, out var normalizedName))
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.CreateProfile,
				ProfileMutationFailure.InvalidProfileName,
				"A Profile name cannot be empty or whitespace.");
		}

		if (snapshot.Profiles.Any(profile => string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.CreateProfile,
				ProfileMutationFailure.ProfileNameConflict,
				$"A Profile named '{normalizedName}' already exists.");
		}

		var now = UtcNow();
		var profile = new Profile(ProfileId.New(), normalizedName, now, null, Array.Empty<ProfileEntry>());
		var updated = snapshot with
		{
			Profiles = snapshot.Profiles.Append(profile).ToArray(),
			ActiveProfileId = makeActive ? profile.Id : snapshot.ActiveProfileId,
			SavedUtc = now,
		};
		return Applied(ProfileMutationOperation.CreateProfile, updated, profile.Id, Array.Empty<ModNodeId>());
	}

	public ProfileMutationResult DeleteProfile(LibrarySnapshot snapshot, ProfileId profileId)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.DeleteProfile, out var context, out var rejected))
			return rejected!;

		var now = UtcNow();
		var profiles = snapshot.Profiles.ToList();
		profiles.RemoveAt(context.Index);
		var updated = snapshot with
		{
			Profiles = profiles,
			ActiveProfileId = snapshot.ActiveProfileId == profileId ? null : snapshot.ActiveProfileId,
			SavedUtc = now,
		};
		return Applied(ProfileMutationOperation.DeleteProfile, updated, profileId, Array.Empty<ModNodeId>());
	}

	public ProfileMutationResult RenameProfile(LibrarySnapshot snapshot, ProfileId profileId, string name)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.RenameProfile, out var context, out var rejected))
			return rejected!;
		if (!TryNormalizeName(name, out var normalizedName))
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.RenameProfile,
				ProfileMutationFailure.InvalidProfileName,
				"A Profile name cannot be empty or whitespace.",
				profileId);
		}

		if (string.Equals(context.Profile.Name, normalizedName, StringComparison.Ordinal))
			return NoChange(ProfileMutationOperation.RenameProfile, snapshot, profileId);

		if (snapshot.Profiles.Any(profile => profile.Id != profileId && string.Equals(profile.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.RenameProfile,
				ProfileMutationFailure.ProfileNameConflict,
				$"A Profile named '{normalizedName}' already exists.",
				profileId);
		}

		var now = UtcNow();
		var updatedProfile = context.Profile with { Name = normalizedName, ModifiedUtc = now };
		var updated = ReplaceProfile(snapshot, context.Index, updatedProfile, now);
		return Applied(ProfileMutationOperation.RenameProfile, updated, profileId, Array.Empty<ModNodeId>());
	}

	public ProfileMutationResult SetActiveProfile(LibrarySnapshot snapshot, ProfileId? profileId)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (profileId is { } requestedId
			&& !TryGetProfile(snapshot, requestedId, ProfileMutationOperation.SetActiveProfile, out _, out var rejected))
		{
			return rejected!;
		}

		if (snapshot.ActiveProfileId == profileId)
			return NoChange(ProfileMutationOperation.SetActiveProfile, snapshot, profileId);

		var now = UtcNow();
		var updated = snapshot with { ActiveProfileId = profileId, SavedUtc = now };
		return Applied(ProfileMutationOperation.SetActiveProfile, updated, profileId, Array.Empty<ModNodeId>());
	}

	public ProfileMutationResult AddEntries(LibrarySnapshot snapshot, ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.AddEntries, out var context, out var rejected))
			return rejected!;
		return InsertNewEntries(snapshot, context, ProfileMutationOperation.AddEntries, nodeIds, context.OrderedEntries.Count);
	}

	public ProfileMutationResult RemoveEntries(LibrarySnapshot snapshot, ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.RemoveEntries, out var context, out var rejected))
			return rejected!;
		if (TryValidateRequestedProfileNodes(snapshot, ProfileMutationOperation.RemoveEntries, profileId, nodeIds, out rejected, validateKinds: true))
			return rejected!;

		var currentIds = context.OrderedEntries.Select(entry => entry.NodeId).ToHashSet();
		var missingEntries = nodeIds.Where(nodeId => !currentIds.Contains(nodeId)).ToArray();
		if (missingEntries.Length > 0)
		{
			var missingEntry = missingEntries[0];
			return Reject(
				snapshot,
				ProfileMutationOperation.RemoveEntries,
				ProfileMutationFailure.EntryNotFound,
				$"Mod '{ToKey(missingEntry)}' is not an entry of Profile '{ToKey(profileId)}'.",
				profileId,
				[missingEntry]);
		}

		var removed = nodeIds.ToHashSet();
		var entries = context.OrderedEntries.Where(entry => !removed.Contains(entry.NodeId)).ToArray();
		return ApplyEntries(snapshot, context, ProfileMutationOperation.RemoveEntries, entries, nodeIds);
	}

	public ProfileMutationResult InsertEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> nodeIds,
		int targetPosition)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.InsertEntries, out var context, out var rejected))
			return rejected!;
		return InsertNewEntries(snapshot, context, ProfileMutationOperation.InsertEntries, nodeIds, targetPosition);
	}

	public ProfileMutationResult MoveEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> nodeIds,
		int targetPosition)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.MoveEntries, out var context, out var rejected))
			return rejected!;
		if (TryValidateRequestedProfileNodes(snapshot, ProfileMutationOperation.MoveEntries, profileId, nodeIds, out rejected, validateKinds: false))
			return rejected!;
		if (targetPosition < 0 || targetPosition > context.OrderedEntries.Count)
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.MoveEntries,
				ProfileMutationFailure.InvalidTargetPosition,
				$"Target position {targetPosition} is outside the valid range 0 through {context.OrderedEntries.Count}.",
				profileId,
				nodeIds);
		}

		var currentIds = context.OrderedEntries.Select(entry => entry.NodeId).ToHashSet();
		var missingEntries = nodeIds.Where(nodeId => !currentIds.Contains(nodeId)).ToArray();
		if (missingEntries.Length > 0)
		{
			var missingEntry = missingEntries[0];
			return Reject(
				snapshot,
				ProfileMutationOperation.MoveEntries,
				ProfileMutationFailure.EntryNotFound,
				$"Mod '{ToKey(missingEntry)}' is not an entry of Profile '{ToKey(profileId)}'.",
				profileId,
				[missingEntry]);
		}

		var selected = nodeIds.ToHashSet();
		var movedInCurrentOrder = context.OrderedEntries.Where(entry => selected.Contains(entry.NodeId)).ToArray();
		var remaining = context.OrderedEntries.Where(entry => !selected.Contains(entry.NodeId)).ToList();
		var removedBeforeTarget = context.OrderedEntries.Take(targetPosition).Count(entry => selected.Contains(entry.NodeId));
		var insertionIndex = Math.Clamp(targetPosition - removedBeforeTarget, 0, remaining.Count);
		remaining.InsertRange(insertionIndex, movedInCurrentOrder);
		return ApplyEntries(snapshot, context, ProfileMutationOperation.MoveEntries, remaining, nodeIds);
	}

	public ProfileMutationResult ReorderEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> orderedNodeIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(orderedNodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.ReorderEntries, out var context, out var rejected))
			return rejected!;

		if (context.OrderedEntries.Count == 0 && orderedNodeIds.Count == 0)
			return NoChange(ProfileMutationOperation.ReorderEntries, snapshot, profileId);

		if (orderedNodeIds.Count != context.OrderedEntries.Count)
		{
			return Reject(
				snapshot,
				ProfileMutationOperation.ReorderEntries,
				ProfileMutationFailure.ReorderMustContainEveryEntry,
				"A full Profile reorder must contain every current Profile entry exactly once.",
				profileId,
				orderedNodeIds);
		}
		if (TryValidateRequestedProfileNodes(snapshot, ProfileMutationOperation.ReorderEntries, profileId, orderedNodeIds, out rejected, validateKinds: false))
			return rejected!;

		var currentById = context.OrderedEntries.ToDictionary(entry => entry.NodeId);
		var missingEntries = orderedNodeIds.Where(nodeId => !currentById.ContainsKey(nodeId)).ToArray();
		if (missingEntries.Length > 0)
		{
			var missingEntry = missingEntries[0];
			return Reject(
				snapshot,
				ProfileMutationOperation.ReorderEntries,
				ProfileMutationFailure.EntryNotFound,
				$"Mod '{ToKey(missingEntry)}' is not an entry of Profile '{ToKey(profileId)}'.",
				profileId,
				[missingEntry]);
		}

		var entries = orderedNodeIds.Select(nodeId => currentById[nodeId]).ToArray();
		return ApplyEntries(snapshot, context, ProfileMutationOperation.ReorderEntries, entries, orderedNodeIds);
	}

	public ProfileMutationResult ReplaceEntries(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> orderedNodeIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(orderedNodeIds);
		if (!TryGetProfile(snapshot, profileId, ProfileMutationOperation.ReplaceEntries, out var context, out var rejected))
			return rejected!;
		if (TryValidateRequestedProfileNodes(
			snapshot,
			ProfileMutationOperation.ReplaceEntries,
			profileId,
			orderedNodeIds,
			out rejected,
			allowEmpty: true,
			validateKinds: true))
		{
			return rejected!;
		}

		var existingById = context.OrderedEntries.ToDictionary(entry => entry.NodeId);
		var now = UtcNow();
		var entries = orderedNodeIds
			.Select((nodeId, index) => existingById.TryGetValue(nodeId, out var existing)
				? existing with { LoadOrder = index }
				: new ProfileEntry(nodeId, index, now))
			.ToArray();
		return ApplyEntries(snapshot, context, ProfileMutationOperation.ReplaceEntries, entries, orderedNodeIds, now);
	}

	private ProfileMutationResult InsertNewEntries(
		LibrarySnapshot snapshot,
		ProfileContext context,
		ProfileMutationOperation operation,
		IReadOnlyList<ModNodeId> nodeIds,
		int targetPosition)
	{
		if (TryValidateRequestedProfileNodes(snapshot, operation, context.Profile.Id, nodeIds, out var rejected, validateKinds: true))
			return rejected!;
		if (targetPosition < 0 || targetPosition > context.OrderedEntries.Count)
		{
			return Reject(
				snapshot,
				operation,
				ProfileMutationFailure.InvalidTargetPosition,
				$"Target position {targetPosition} is outside the valid range 0 through {context.OrderedEntries.Count}.",
				context.Profile.Id,
				nodeIds);
		}

		var currentIds = context.OrderedEntries.Select(entry => entry.NodeId).ToHashSet();
		var presentEntries = nodeIds.Where(currentIds.Contains).ToArray();
		if (presentEntries.Length > 0)
		{
			var present = presentEntries[0];
			return Reject(
				snapshot,
				operation,
				ProfileMutationFailure.EntryAlreadyPresent,
				$"Mod '{ToKey(present)}' is already an entry of Profile '{ToKey(context.Profile.Id)}'.",
				context.Profile.Id,
				[present]);
		}

		var now = UtcNow();
		var entries = context.OrderedEntries.ToList();
		entries.InsertRange(targetPosition, nodeIds.Select(nodeId => new ProfileEntry(nodeId, targetPosition, now)));
		return ApplyEntries(snapshot, context, operation, entries, nodeIds, now);
	}

	private ProfileMutationResult ApplyEntries(
		LibrarySnapshot snapshot,
		ProfileContext context,
		ProfileMutationOperation operation,
		IReadOnlyList<ProfileEntry> requestedEntries,
		IReadOnlyList<ModNodeId> affectedNodeIds,
		DateTimeOffset? timestamp = null)
	{
		var normalized = NormalizeEntries(requestedEntries);
		if (normalized.SequenceEqual(context.Profile.Entries))
			return NoChange(operation, snapshot, context.Profile.Id, affectedNodeIds);

		var now = timestamp ?? UtcNow();
		var profile = context.Profile with
		{
			Entries = normalized,
			ModifiedUtc = now,
			Revision = checked(context.Profile.Revision + 1),
		};
		var updated = ReplaceProfile(snapshot, context.Index, profile, now);
		return Applied(operation, updated, context.Profile.Id, affectedNodeIds);
	}

	private static bool TryGetProfile(
		LibrarySnapshot snapshot,
		ProfileId profileId,
		ProfileMutationOperation operation,
		out ProfileContext context,
		out ProfileMutationResult? rejected)
	{
		var index = snapshot.Profiles.ToList().FindIndex(profile => profile.Id == profileId);
		if (index < 0)
		{
			context = default!;
			rejected = Reject(
				snapshot,
				operation,
				ProfileMutationFailure.ProfileNotFound,
				$"Profile '{ToKey(profileId)}' does not exist in the supplied library snapshot.",
				profileId);
			return false;
		}

		var profile = snapshot.Profiles[index];
		var duplicate = profile.Entries.GroupBy(entry => entry.NodeId).FirstOrDefault(group => group.Count() > 1);
		if (duplicate is not null)
		{
			context = default!;
			rejected = Reject(
				snapshot,
				operation,
				ProfileMutationFailure.ProfileContainsDuplicateEntries,
				$"Profile '{ToKey(profileId)}' contains duplicate entry '{ToKey(duplicate.Key)}'.",
				profileId,
				[duplicate.Key]);
			return false;
		}

		foreach (var entry in profile.Entries)
		{
			if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node))
			{
				context = default!;
				rejected = Reject(
					snapshot,
					operation,
					ProfileMutationFailure.ProfileEntryNodeMissing,
					$"Profile '{ToKey(profileId)}' references missing Mod '{ToKey(entry.NodeId)}'.",
					profileId,
					[entry.NodeId]);
				return false;
			}
			if (GetProfileKindFailure(node) is { })
			{
				context = default!;
				rejected = Reject(
					snapshot,
					operation,
					ProfileMutationFailure.ProfileContainsNonDeployableEntry,
					$"Profile '{ToKey(profileId)}' contains a Mod kind that cannot be a top-level Profile entry: '{ToKey(node.Id)}' ({node.Metadata.Kind}).",
					profileId,
					[node.Id]);
				return false;
			}
		}

		context = new ProfileContext(index, profile, OrderEntries(profile.Entries));
		rejected = null;
		return true;
	}

	private static bool TryValidateRequestedProfileNodes(
		LibrarySnapshot snapshot,
		ProfileMutationOperation operation,
		ProfileId profileId,
		IReadOnlyList<ModNodeId> nodeIds,
		out ProfileMutationResult? rejected,
		bool allowEmpty = false,
		bool validateKinds = true)
	{
		if (!allowEmpty && nodeIds.Count == 0)
		{
			rejected = Reject(
				snapshot,
				operation,
				ProfileMutationFailure.EmptyEntrySelection,
				"At least one Mod entry must be selected.",
				profileId);
			return true;
		}

		var known = new HashSet<ModNodeId>();
		foreach (var nodeId in nodeIds)
		{
			if (!known.Add(nodeId))
			{
				rejected = Reject(
					snapshot,
					operation,
					ProfileMutationFailure.DuplicateEntrySelection,
					$"Mod '{ToKey(nodeId)}' was selected more than once.",
					profileId,
					[nodeId]);
				return true;
			}
			if (!snapshot.Nodes.TryGetValue(nodeId, out var node))
			{
				rejected = Reject(
					snapshot,
					operation,
					ProfileMutationFailure.NodeNotFound,
					$"Mod '{ToKey(nodeId)}' does not exist in the supplied library snapshot.",
					profileId,
					[nodeId]);
				return true;
			}
			if (validateKinds && GetProfileKindFailure(node) is { } failure)
			{
				rejected = Reject(
					snapshot,
					operation,
					failure,
					BuildProfileEntryMessage(node),
					profileId,
					[nodeId]);
				return true;
			}
		}

		rejected = null;
		return false;
	}

	private static ProfileMutationFailure? GetProfileKindFailure(ModNode node)
		=> node.Metadata.Kind switch
		{
			ModNodeKind.Standard or ModNodeKind.ReShade => null,
			_ => ProfileMutationFailure.NodeKindNotSupported,
		};

	private static string BuildProfileEntryMessage(ModNode node)
		=> $"{node.Metadata.Kind} Mod '{ToKey(node.Id)}' cannot be added to a Profile.";

	private static IReadOnlyList<ProfileEntry> OrderEntries(IReadOnlyList<ProfileEntry> entries)
		=> entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value)
			.ToArray();

	private static IReadOnlyList<ProfileEntry> NormalizeEntries(IReadOnlyList<ProfileEntry> entries)
		=> entries
			.Select((entry, index) => entry with { LoadOrder = index })
			.ToArray();

	private static LibrarySnapshot ReplaceProfile(LibrarySnapshot snapshot, int index, Profile profile, DateTimeOffset now)
	{
		var profiles = snapshot.Profiles.ToArray();
		profiles[index] = profile;
		return snapshot with { Profiles = profiles, SavedUtc = now };
	}

	private static bool TryNormalizeName(string? name, out string normalized)
	{
		normalized = name?.Trim() ?? string.Empty;
		return !string.IsNullOrWhiteSpace(normalized);
	}

	private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

	private static ProfileMutationResult Applied(
		ProfileMutationOperation operation,
		LibrarySnapshot snapshot,
		ProfileId? profileId,
		IReadOnlyList<ModNodeId> entryIds)
		=> new(operation, ProfileMutationStatus.Applied, snapshot, profileId, Freeze(entryIds));

	private static ProfileMutationResult NoChange(
		ProfileMutationOperation operation,
		LibrarySnapshot snapshot,
		ProfileId? profileId,
		IReadOnlyList<ModNodeId>? entryIds = null)
		=> new(operation, ProfileMutationStatus.NoChange, snapshot, profileId, Freeze(entryIds ?? Array.Empty<ModNodeId>()));

	private static ProfileMutationResult Reject(
		LibrarySnapshot snapshot,
		ProfileMutationOperation operation,
		ProfileMutationFailure failure,
		string message,
		ProfileId? profileId = null,
		IReadOnlyList<ModNodeId>? entryIds = null)
		=> new(operation, ProfileMutationStatus.Rejected, snapshot, profileId, Freeze(entryIds ?? Array.Empty<ModNodeId>()), failure, message);

	private static IReadOnlyList<ModNodeId> Freeze(IReadOnlyList<ModNodeId> values)
		=> Array.AsReadOnly(values.ToArray());

	private static string ToKey(ProfileId id) => id.Value.ToString("N");

	private static string ToKey(ModNodeId id) => id.Value.ToString("N");

	private sealed record ProfileContext(int Index, Profile Profile, IReadOnlyList<ProfileEntry> OrderedEntries);
}
