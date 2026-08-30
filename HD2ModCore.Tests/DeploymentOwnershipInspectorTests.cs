using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Locks the CLI-facing invariant that only completely proven manager-owned patches may be deleted.
public sealed class DeploymentOwnershipInspectorTests
{
	[Fact]
	public async Task InspectAsync_EmptyDirectoryWithoutState_IsCleanBootstrap()
	{
		using var fixture = new GameDataFixture();

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.CleanBootstrap, result.Status);
		Assert.Empty(result.ActualPatches);
		Assert.Empty(result.SafeDeletionTargets);
		Assert.DoesNotContain(result.Issues, issue => issue.Severity == CoreIssueSeverity.Error);
	}

	[Fact]
	public async Task InspectAsync_CompleteMatchingState_IsManagedAndExposesExactDeletionTargets()
	{
		using var fixture = new GameDataFixture();
		var patch = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "PATCH");
		var stream = await fixture.WritePatchAsync("0123456789abcdef.patch_0.stream", "STREAM");
		var state = new ActivationState(
			JsonActivationStateStore.CurrentVersion,
			ProfileId.New(),
			7,
			DateTimeOffset.UtcNow,
			true,
			[
				fixture.EntryFor(patch, "0123456789abcdef", 0, PatchSidecarKind.Base),
				fixture.EntryFor(stream, "0123456789abcdef", 0, PatchSidecarKind.Stream),
			],
			[]);
		await fixture.SaveStateAsync(state);

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Managed, result.Status);
		Assert.Equal(new[] { patch, stream }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), result.ActualPatches.Select(patch => patch.Path));
		Assert.Equal(state.Files, result.SafeDeletionTargets);
		Assert.NotNull(result.ActivationState);
		Assert.Equal(state.ProfileId, result.ActivationState!.ProfileId);
		Assert.Equal(state.ProfileRevision, result.ActivationState.ProfileRevision);
	}

	[Fact]
	public async Task InspectAsync_PatchWithoutState_IsBlockedAsUnmanaged()
	{
		using var fixture = new GameDataFixture();
		var patch = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "PATCH");

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.ActualPatches, item => item.Path == patch);
		Assert.Contains(result.Issues, issue => issue.Code == "ActivationStateMissingForPatches");
		Assert.Empty(result.SafeDeletionTargets);
	}

	[Fact]
	public async Task InspectAsync_ChangedManagedPatch_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var patch = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "ORIGINAL");
		await fixture.SaveStateAsync(fixture.StateFor(fixture.EntryFor(patch, "0123456789abcdef", 0, PatchSidecarKind.Base)));
		await File.WriteAllTextAsync(patch, "CHANGED");

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ManagedPatchHashMismatch");
		Assert.Empty(result.SafeDeletionTargets);
	}

	[Fact]
	public async Task InspectAsync_MissingRecordedHash_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var patch = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "PATCH");
		var entry = fixture.EntryFor(patch, "0123456789abcdef", 0, PatchSidecarKind.Base) with { ContentSha256 = string.Empty };
		await fixture.SaveStateAsync(fixture.StateFor(entry));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ManagedPatchHashMissing");
	}

	[Fact]
	public async Task InspectAsync_UnknownActualPatch_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var registered = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "REGISTERED");
		var unknown = await fixture.WritePatchAsync("fedcba9876543210.patch_0", "UNKNOWN");
		await fixture.SaveStateAsync(fixture.StateFor(fixture.EntryFor(registered, "0123456789abcdef", 0, PatchSidecarKind.Base)));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "UnmanagedPatchDetected" && issue.FilePath == unknown);
	}

	[Fact]
	public async Task InspectAsync_MissingRegisteredPatch_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var missing = Path.Combine(fixture.GameDataDirectory, "0123456789abcdef.patch_0");
		await fixture.SaveStateAsync(fixture.StateFor(fixture.EntryFor(missing, "0123456789abcdef", 0, PatchSidecarKind.Base, "PATCH")));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ManagedPatchMissing" && issue.FilePath == missing);
	}

	[Fact]
	public async Task InspectAsync_TargetOutsideGameData_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var outside = Path.Combine(fixture.RootDirectory, "0123456789abcdef.patch_0");
		await File.WriteAllTextAsync(outside, "PATCH");
		await fixture.SaveStateAsync(fixture.StateFor(fixture.EntryFor(outside, "0123456789abcdef", 0, PatchSidecarKind.Base)));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ActivationStateTargetNotTopLevel");
	}

	[Fact]
	public async Task InspectAsync_DuplicateStateEntry_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		var patch = await fixture.WritePatchAsync("0123456789abcdef.patch_0", "PATCH");
		var entry = fixture.EntryFor(patch, "0123456789abcdef", 0, PatchSidecarKind.Base);
		await fixture.SaveStateAsync(fixture.StateFor(entry, entry));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "DuplicateActivationStateTarget");
		Assert.Contains(result.Issues, issue => issue.Code == "DuplicateActivationStatePatchIdentity");
	}

	[Fact]
	public async Task InspectAsync_IncompleteOrUnsupportedState_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		await fixture.SaveStateAsync(new ActivationState(
			JsonActivationStateStore.CurrentVersion,
			null,
			0,
			DateTimeOffset.UtcNow,
			false,
			[],
			[]));

		var incomplete = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, incomplete.Status);
		Assert.Contains(incomplete.Issues, issue => issue.Code == "ActivationStateIncomplete");

		await File.WriteAllTextAsync(
			Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName),
			"{\"version\":999,\"profileId\":null,\"profileRevision\":0,\"appliedUtc\":\"2026-08-30T00:00:00+00:00\",\"completed\":true,\"files\":[],\"issues\":[]}");

		var unsupported = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, unsupported.Status);
		Assert.Contains(unsupported.Issues, issue => issue.Code == "ActivationStateVersionUnsupported");
	}

	[Fact]
	public async Task InspectAsync_MalformedState_IsBlocked()
	{
		using var fixture = new GameDataFixture();
		await File.WriteAllTextAsync(
			Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName),
			"{ this is not json }");

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ActivationStateInvalid");
	}

	[Fact]
	public async Task InspectAsync_ReparsePointPatch_IsBlockedWhenTheFileSystemSupportsIt()
	{
		using var fixture = new GameDataFixture();
		var source = Path.Combine(fixture.RootDirectory, "source.patch");
		await File.WriteAllTextAsync(source, "PATCH");
		var target = Path.Combine(fixture.GameDataDirectory, "0123456789abcdef.patch_0");
		try
		{
			File.CreateSymbolicLink(target, source);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
		{
			return;
		}

		await fixture.SaveStateAsync(fixture.StateFor(fixture.EntryFor(target, "0123456789abcdef", 0, PatchSidecarKind.Base, "PATCH")));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Blocked, result.Status);
		Assert.Contains(result.Issues, issue => issue.Code == "ManagedPatchUnexpectedReparsePoint");
	}

	[Fact]
	public async Task InspectAsync_RecordedSymbolicLink_IsManagedWhenItStillTargetsTheRecordedSource()
	{
		using var fixture = new GameDataFixture();
		var source = Path.Combine(fixture.RootDirectory, "source.patch");
		await File.WriteAllTextAsync(source, "PATCH");
		var target = Path.Combine(fixture.GameDataDirectory, "0123456789abcdef.patch_0");
		try
		{
			File.CreateSymbolicLink(target, source);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
		{
			return;
		}

		var entry = fixture.EntryFor(target, "0123456789abcdef", 0, PatchSidecarKind.Base, "PATCH") with
		{
			SourcePath = source,
			Method = DeploymentMethod.SymbolicLink,
		};
		await fixture.SaveStateAsync(fixture.StateFor(entry));

		var result = await new DeploymentOwnershipInspector().InspectAsync(fixture.GameDataDirectory);

		Assert.Equal(DeploymentOwnershipStatus.Managed, result.Status);
		Assert.True(Assert.Single(result.ActualPatches).IsReparsePoint);
	}

	private sealed class GameDataFixture : IDisposable
	{
		public GameDataFixture()
		{
			RootDirectory = Path.Combine(Path.GetTempPath(), "hd2-deployment-ownership-" + Guid.NewGuid().ToString("N"));
			GameDataDirectory = Path.Combine(RootDirectory, "data");
			Directory.CreateDirectory(GameDataDirectory);
		}

		public string RootDirectory { get; }

		public string GameDataDirectory { get; }

		public async Task<string> WritePatchAsync(string fileName, string content)
		{
			var path = Path.Combine(GameDataDirectory, fileName);
			await File.WriteAllTextAsync(path, content);
			return path;
		}

		public ActivationState StateFor(params ActivationStateFileEntry[] files)
			=> new(
				JsonActivationStateStore.CurrentVersion,
				ProfileId.New(),
				1,
				DateTimeOffset.UtcNow,
				true,
				files,
				[]);

		public ActivationStateFileEntry EntryFor(
			string targetPath,
			string archiveHex16,
			int targetPatchIndex,
			PatchSidecarKind sidecarKind,
			string? expectedContent = null)
		{
			var content = expectedContent ?? File.ReadAllText(targetPath);
			return new ActivationStateFileEntry(
				targetPath,
				Path.Combine(RootDirectory, "source.patch"),
				DeploymentMethod.Copy,
				archiveHex16,
				0,
				targetPatchIndex,
				sidecarKind,
				ModNodeId.New(),
				Encoding.UTF8.GetByteCount(content),
				ToSha256(content));
		}

		public Task SaveStateAsync(ActivationState state)
			=> new JsonActivationStateStore().SaveAsync(GameDataDirectory, state).AsTask();

		public void Dispose()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
		}

		private static string ToSha256(string content)
			=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
	}
}
