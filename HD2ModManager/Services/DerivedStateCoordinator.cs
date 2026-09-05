using System.IO;
using HD2ModCore.Domain;
using HD2ModCore.Application;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

// Purpose: Shares immutable cached Mod-derived facts across pages and rebuilds only generations invalidated by library, profile, deployment or mapping changes.
public sealed class DerivedStateCoordinator : IAsyncDisposable
{
    private readonly ModLibraryService _library;
    private readonly ProfileService _profiles;
    private readonly StoragePaths _paths;
    private readonly IModInformationCenter _informationCenter;
    private readonly IProfileOverrideGraphService _profileGraph;
	private readonly IProfileMaterialDiagnosticsService _profileMaterialDiagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private DerivedStateSnapshot _snapshot = DerivedStateSnapshot.Empty;
    private bool _contentDirty = true;
    private bool _expectedDirty = true;
    private bool _deployedDirty = true;
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshRequested;
    private (ProfileId? Id, long Revision) _activeProfileSignature;
    private long _refreshVersion;

    public DerivedStateSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public IModInformationCenter InformationCenter => _informationCenter;
    public event EventHandler<DerivedStateSnapshot>? SnapshotChanged;

    public DerivedStateCoordinator(ModLibraryService library, ProfileService profiles, IModInformationCenter informationCenter)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _paths = SettingsService.CreateStoragePaths();
        _informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
        _profileGraph = CoreServices.CreateProfileOverrideGraphService(_paths, _informationCenter);
        _profileMaterialDiagnostics = CoreServices.CreateProfileMaterialDiagnosticsService(_paths, _informationCenter);
        _activeProfileSignature = GetActiveProfileSignature(GetEvaluationSnapshot());
        _profiles.Changed += OnProfilesChanged;
        _library.ModContentFactsChanged += OnContentChanged;
        _library.ModRelationshipsChanged += OnModRelationshipsChanged;
        _library.OptionActivationChanged += OnOptionActivationChanged;
    }

    // Player-facing status is profile-derived. Deployment validation is an explicit diagnostic, not a page-open scan.
    public void MarkDeploymentDirty() { }
    public void MarkMappingDirty() => MarkDirty(content: false, expected: true, deployed: false);
    public void MarkContentDirty() => MarkDirty(content: true, expected: true, deployed: false);

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _refreshRequested = true;
            var version = ++_refreshVersion;
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return RefreshCoreAsync(_refreshCancellation.Token, version);
        }
    }

    public IReadOnlyDictionary<ModNodeId, ModUserStatus> ProjectStatuses(ProfileId? selectedProfileId)
    {
        var snapshot = Snapshot;
        return ModUserStatusProjector.Project(
            GetEvaluationSnapshot(),
            selectedProfileId,
            snapshot.ContentFacts,
            snapshot.ExpectedGraph,
            snapshot.MaterialDiagnostics,
            snapshot.DeployedGraph,
            _library.GetOptionActivationSnapshot());
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken, long refreshVersion)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ProfileService and ModLibraryService deliberately have separate
            // write paths. A Profile mutation reaches ProfileService first;
            // a relation mutation reaches ModLibraryService first. Build one
            // coherent evaluation snapshot from the newer profile payload and
            // the authoritative current node graph so an Option relation can
            // never be projected using yesterday's parent graph.
            var profileSnapshot = GetEvaluationSnapshot();
            var profileSignature = GetActiveProfileSignature(profileSnapshot);
            var librarySnapshotSignature = _library.Snapshot.SavedUtc;
            var current = Snapshot;
            IReadOnlyDictionary<ModNodeId, ModContentFacts> content = current.ContentFacts;
            ProfileOverrideGraph? expected = current.ExpectedGraph;
			ProfileMaterialDiagnostics? materialDiagnostics = current.MaterialDiagnostics;
            DeployedOverrideGraph? deployed = current.DeployedGraph;
            bool contentDirty;
            bool expectedDirty;
            lock (_sync)
            {
                contentDirty = _contentDirty;
                expectedDirty = _expectedDirty;
            }

            var active = profileSnapshot.ActiveProfileId is { } activeId ? profileSnapshot.Profiles.FirstOrDefault(profile => profile.Id == activeId) : null;
            var effectiveActive = active is null
                ? null
                : ProfileEffectiveProfileResolver.Resolve(active, profileSnapshot, _library.GetOptionActivationSnapshot());
            var activeNodeIds = effectiveActive?.Entries.Select(entry => entry.NodeId).Where(profileSnapshot.Nodes.ContainsKey).ToHashSet()
                ?? new HashSet<ModNodeId>();
            contentDirty |= !SameNodeSet(content.Keys, activeNodeIds);

            if (contentDirty)
            {
                content = activeNodeIds.Count == 0
                    ? new Dictionary<ModNodeId, ModContentFacts>()
                    : await GetAssetInventoryAsync(profileSnapshot, activeNodeIds, cancellationToken).ConfigureAwait(false);
                expectedDirty = true;
            }

            if (active is null)
            {
                expected = null;
				materialDiagnostics = null;
				deployed = null;
            }
            else if (expectedDirty || !IsExpectedCurrent(expected, active, content))
            {
                var effectiveProfile = effectiveActive ?? active;
                expected = await _profileGraph.BuildAsync(effectiveProfile, profileSnapshot, _library.ModsRootDirectory, cancellationToken).ConfigureAwait(false);
				materialDiagnostics = await _profileMaterialDiagnostics.BuildAsync(effectiveProfile, profileSnapshot, _library.ModsRootDirectory, cancellationToken).ConfigureAwait(false);
            }

            // Do not scan GameData while a profile page is opening. It cannot affect the four player states.
            deployed = null;

            var next = new DerivedStateSnapshot(content, expected, materialDiagnostics, deployed, DateTimeOffset.UtcNow, null);
            lock (_sync)
            {
                if (refreshVersion != _refreshVersion
                    || profileSignature != GetActiveProfileSignature(GetEvaluationSnapshot())
                    || librarySnapshotSignature != _library.Snapshot.SavedUtc)
                    return;
                _contentDirty = false;
                _expectedDirty = false;
                _deployedDirty = false;
                _snapshot = next;
            }
            SnapshotChanged?.Invoke(this, next);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (refreshVersion != _refreshVersion) return;
            }
            var failed = Snapshot with { LastError = exception.Message };
            lock (_sync) _snapshot = failed;
            SnapshotChanged?.Invoke(this, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask<IReadOnlyDictionary<ModNodeId, ModContentFacts>> GetAssetInventoryAsync(
        LibrarySnapshot snapshot,
        IReadOnlySet<ModNodeId> nodeIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<ModNodeId, ModContentFacts>();
        foreach (var nodeId in nodeIds)
        {
            if (!snapshot.Nodes.TryGetValue(nodeId, out var node)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var facts = _library.GetDerivedData(nodeId.Value.ToString("N"))?.ContentFacts;
            if (facts is not null) result[nodeId] = facts;
        }
        return ValueTask.FromResult<IReadOnlyDictionary<ModNodeId, ModContentFacts>>(result);
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        var current = GetActiveProfileSignature(GetEvaluationSnapshot());
        var activeChanged = current != _activeProfileSignature;
        _activeProfileSignature = current;
        MarkDirty(content: false, expected: activeChanged, deployed: false);
    }
    private void OnContentChanged(object? sender, ModContentFactsChangedEventArgs e)
    {
        if (e.Kind == ModContentChangeKind.DerivedOnly) return;

        var evaluation = GetEvaluationSnapshot();
        var active = GetActiveProfile(evaluation);
        var activeRoutes = active is null
            ? ProfileOptionRouteResolution.Empty
            : ProfileOptionRouteResolver.Resolve(active, evaluation, _library.GetOptionActivationSnapshot());
        var affectsActiveProfile = active is not null && e.NodeIds.Any(nodeId =>
            active.Entries.Any(entry => entry.NodeId == nodeId)
            || activeRoutes.IsNodeReachable(nodeId));
        if (affectsActiveProfile)
        {
            MarkDirty(content: true, expected: true, deployed: false);
        }
    }

    private void OnModRelationshipsChanged(object? sender, ModRelationshipMutationResult e)
    {
        // A relationship mutation can change the effective node set even when
        // no Patch bytes changed: a physical Option may have been added to or
        // removed from an active Profile, or a logical anchor may have gained
        // descendants. Invalidate the shared projection at the same boundary as
        // the library mutation instead of waiting for a page to be reopened.
        MarkDirty(content: false, expected: true, deployed: false);
    }

    private void OnOptionActivationChanged(object? sender, EventArgs e)
    {
        // Option activation is host-scoped and is persisted outside
        // LibrarySnapshot. It therefore cannot be detected by the snapshot
        // signature alone, but it directly changes effective deployment and
        // user-status projections for shared Options.
        MarkDirty(content: false, expected: true, deployed: false);
    }

    private void MarkDirty(bool content, bool expected, bool deployed)
    {
        var refreshRequested = false;
        lock (_sync)
        {
            _contentDirty |= content;
            _expectedDirty |= expected;
            _deployedDirty |= deployed;
            refreshRequested = _refreshRequested;
        }
        // Startup receives profile and deployment events before any page needs derived
        // diagnostics. Keep the dirty state, but do not materialize every active Mod yet.
        if (refreshRequested) _ = RefreshAsync();
    }

    private static bool SameNodeSet(IEnumerable<ModNodeId> left, IEnumerable<ModNodeId> right)
        => left.OrderBy(id => id.Value).SequenceEqual(right.OrderBy(id => id.Value));

    private LibrarySnapshot GetEvaluationSnapshot()
    {
        var library = _library.Snapshot;
        var profiles = _profiles.Snapshot;

        // If a profile write committed most recently, use its profile list and
        // active selection. If a library/relation mutation committed most
        // recently, it already contains the canonical Profile updates produced
        // by the compound relationship transaction. Nodes always come from the
        // library service because it owns relationship metadata and never lags
        // a ModRelationshipMutationResult.
        var profileSource = profiles.SavedUtc > library.SavedUtc ? profiles : library;
        return profileSource with { Nodes = library.Nodes };
    }

    private static Profile? GetActiveProfile(LibrarySnapshot snapshot)
        => snapshot.ActiveProfileId is { } activeId
            ? snapshot.Profiles.FirstOrDefault(profile => profile.Id == activeId)
            : null;

    private static bool IsExpectedCurrent(ProfileOverrideGraph? graph, Profile active, IReadOnlyDictionary<ModNodeId, ModContentFacts> content)
        => graph is not null && graph.ProfileId == active.Id && graph.ProfileRevision == active.Revision;

    private static (ProfileId? Id, long Revision) GetActiveProfileSignature(LibrarySnapshot snapshot)
    {
        var active = GetActiveProfile(snapshot);
        return active is null ? (null, 0) : (active.Id, active.Revision);
    }

    public async ValueTask DisposeAsync()
    {
        _profiles.Changed -= OnProfilesChanged;
        _library.ModContentFactsChanged -= OnContentChanged;
        _library.ModRelationshipsChanged -= OnModRelationshipsChanged;
        _library.OptionActivationChanged -= OnOptionActivationChanged;
        lock (_sync)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }
}

public sealed record DerivedStateSnapshot(
    IReadOnlyDictionary<ModNodeId, ModContentFacts> ContentFacts,
    ProfileOverrideGraph? ExpectedGraph,
	ProfileMaterialDiagnostics? MaterialDiagnostics,
    DeployedOverrideGraph? DeployedGraph,
    DateTimeOffset BuiltUtc,
    string? LastError)
{
    public static DerivedStateSnapshot Empty { get; } = new(new Dictionary<ModNodeId, ModContentFacts>(), null, null, null, DateTimeOffset.MinValue, null);
}
