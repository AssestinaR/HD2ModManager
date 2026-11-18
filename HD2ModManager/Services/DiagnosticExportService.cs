using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Diagnostics;
using HD2ModCore.Updates;
using Microsoft.Win32;

namespace HD2ModManager.Services;

internal sealed record DiagnosticSource(string Status, object? Data = null, int Records = 0, string? Reason = null,
	bool Truncated = false, int Omitted = 0);

internal sealed record DiagnosticSettings(string? GameData, string? ExportPath, object Values);

internal sealed record DiagnosticCapture(DateTimeOffset CapturedAtUtc, DiagnosticSettings Settings, string ModsRoot,
	LibrarySnapshot Library, OptionActivationSnapshot Options, ProfileDeploymentStatus Deployment,
	GameDataIndexStatus? LastIndexStatus, AppUpdateView? Update, object[] Tasks);

internal static class DiagnosticExportService
{
	private sealed record LuaEvidence(DateTimeOffset ModifiedAtUtc, object? Status, IReadOnlyList<DiagnosticLogFact> Recent,
		object Counts);
	private sealed record DeploymentEvidence(object Desired, CommittedDeployment? Committed = null);
	private sealed record CommittedDeployment(string? Profile, long ProfileRevision, DateTimeOffset AppliedUtc, bool Completed,
		bool MatchesActiveRevision, int FileCount, int? ExternalCount, object[] Files, object[] Issues);
	private sealed class Aliases
	{
		private readonly Dictionary<Guid, string> profiles = new();
		private readonly Dictionary<Guid, string> mods = new();
		internal string? Profile(Guid? id) => Get(id, profiles, "Profile");
		internal string? Mod(Guid? id) => Get(id, mods, "Mod");
		private static string? Get(Guid? id, Dictionary<Guid, string> aliases, string prefix)
		{
			if (id is not { } value || value == Guid.Empty) return null;
			if (!aliases.TryGetValue(value, out var alias)) aliases[value] = alias = $"{prefix}-{aliases.Count + 1}";
			return alias;
		}
	}

	internal static async Task<int> ExportAsync(string path, DiagnosticCapture capture, IProgress<string> progress, CancellationToken cancellationToken)
	{
		var since = capture.CapturedAtUtc - DiagnosticBundleWriter.History;
		var until = capture.CapturedAtUtc;
		var aliases = new Aliases();
		var baseDirectory = AppContext.BaseDirectory;
		var logRoot = Path.Combine(baseDirectory, "logs");
		var sources = new Dictionary<string, Task<DiagnosticSource>>
		{
			["environment"] = CollectAsync(token => ReadEnvironmentAsync(capture, baseDirectory, token), cancellationToken),
			["settingsFile"] = CollectAsync(token => ReadSettingsStateAsync(Path.Combine(baseDirectory, "settings.json"), token), cancellationToken),
			["displayAdapters"] = CollectAsync(ReadDisplayAdaptersAsync, cancellationToken),
			["assetIndex"] = CollectAsync(token => ReadIndexAsync(capture, baseDirectory, token), cancellationToken),
			["deployment"] = CollectAsync(token => ReadDeploymentAsync(capture, aliases, token), cancellationToken),
			["operationHistory"] = CollectAsync(async token =>
			{
				var history = await DiagnosticOperations.Journal.ReadAsync(since, token).ConfigureAwait(false);
				var events = history.Events.Where(item => item.AtUtc <= until).ToArray();
				return new DiagnosticSource(history.StorageUnavailable ? "partial" : events.Length == 0 ? "noMatch" : "collected", events, events.Length,
					history.StorageUnavailable ? "LocalJournalUnavailable" : null, history.Truncated, history.InvalidRecords);
			}, cancellationToken),
			["managerLog"] = CollectAsync(token => ReadManagerLogsAsync(logRoot, "manager", since, until, token), cancellationToken),
			["traceLog"] = CollectAsync(token => ReadManagerLogsAsync(logRoot, "trace", since, until, token), cancellationToken),
			["updateLog"] = CollectAsync(token => ReadUpdateLogAsync(Path.Combine(logRoot, "app-update.log"), since, until, token), cancellationToken),
			["windowsEvents"] = CollectAsync(token => DiagnosticGameEvidence.ReadWindowsEventsAsync(since, until, token), cancellationToken),
			["gameNativeReport"] = CollectAsync(token => DiagnosticGameEvidence.ReadNativeReportsAsync(since, until, token), cancellationToken),
		};
		var luaRoot = LocalLuaRoot();
		var commonLuaRoot = CommonLuaRoot();
		var externalLoaderConfigured = LuaDeploymentService.EffectiveMappingMode == ArmorMappingMode.ExternalLuaEntry
			|| LuaDeploymentService.EffectivePhysicsMode == PhysicsMode.ExternalLuaEntry;
		foreach (var name in new[] { "ArmorRegistryRuntime.log", "ArmorOverrideRuntime.log", "KitInstanceHider.log", "MDL-Boot.log", "runtime.status", "bank-patch.status" })
		{
			var isLog = name.EndsWith(".log", StringComparison.Ordinal);
			var localPath = luaRoot is null ? null : Path.Combine(luaRoot, isLog ? "Logs" : "ArmorRegistry", name);
			var commonPath = isLog && commonLuaRoot is not null ? Path.Combine(commonLuaRoot, "Logs", name) : null;
			var sourcePath = isLog ? DiagnosticFiles.SelectNewestExistingPath(localPath, commonPath) : localPath;
			var optionalExternalOnly = name == "MDL-Boot.log" && !externalLoaderConfigured;
			sources.Add(name, CollectAsync(token => ReadLuaAsync(sourcePath, since, until, optionalExternalOnly, token), cancellationToken));
		}
		sources.Add("MDL3.log", CollectAsync(token => ReadMdl3LogAsync(since, until, token), cancellationToken));

		await Task.WhenAll(sources.Values).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		var results = sources.ToDictionary(item => item.Key, item => item.Value.GetAwaiter().GetResult(), StringComparer.Ordinal);
		var historyEvents = results["operationHistory"].Data as DiagnosticEvent[] ?? [];
		var crashes = results["windowsEvents"].Data as List<DiagnosticGameEvidence.CrashEvent> ?? [];
		var logFacts = new[] { "managerLog", "traceLog", "updateLog", "MDL3.log" }.SelectMany(key =>
			(results[key].Data as IReadOnlyList<DiagnosticLogFact> ?? []).Select(fact => (Source: key, Fact: fact))).ToArray();
		var legacy = logFacts.Select(item => (At: item.Fact.AtUtc ?? until, Row: (object)new { source = item.Source, item.Fact }));
		var operations = historyEvents.Select(item => (At: item.AtUtc, Row: (object)new
		{
			source = "operationHistory", item.AtUtc, item.Session, item.Sequence, item.Operation, item.ParentOperation,
			item.Action, item.Result, item.Trigger, profile = aliases.Profile(item.Profile), item.Revision, mod = aliases.Mod(item.Mod),
			item.Count, item.Code, item.ErrorNumber, item.DurationMs,
		}));
		var windowsEvents = crashes.Select(item => (At: item.AtUtc, Row: (object)new { source = "windowsEvents", item }));
		var timeline = operations.Concat(legacy).Concat(windowsEvents);
		if (results["deployment"].Data is DeploymentEvidence { Committed: { } committed }
			&& committed.AppliedUtc >= since && committed.AppliedUtc <= until)
		{
			timeline = timeline.Append((committed.AppliedUtc, new
			{
				source = "deployment", atUtc = committed.AppliedUtc, committed.Profile, committed.ProfileRevision, committed.Completed,
			}));
		}
		var settings = new
		{
			capture.CapturedAtUtc, capture.Settings.Values, environment = results["environment"].Data,
			displayAdapters = results["displayAdapters"].Data,
			assetIndex = results["assetIndex"].Data, libraryNodes = capture.Library.Nodes.Count,
			update = capture.Update is { } update ? new
			{
				current = AppVersion.Normalize(update.Current), update.CanInstall, update.Prefs.AutoCheck, update.Prefs.AutoUpdate, update.Prefs.DownloadSource,
				check = new { update.Check.Phase, update.Check.At, update.Check.Code, update.Check.DigestSource, offeredVersion = AppVersion.Normalize(update.Check.Release?.Version) },
				download = new { update.Download.Phase, update.Download.Code, update.Download.DigestSource, version = AppVersion.Normalize(update.Download.Version) },
			} : null, tasks = capture.Tasks,
		};
		var gapCount = results.Values.Count(result => result.Status is not ("collected" or "noMatch" or "notApplicable"));
		var entries = new Dictionary<string, byte[]>
		{
			["summary.txt"] = Encoding.UTF8.GetBytes(Summarize(capture, since, results, historyEvents, crashes, logFacts, aliases)),
			["manifest.json"] = DiagnosticBundleWriter.Serialize(new
			{
				schemaVersion = 1, privacyVersion = 1, capture.CapturedAtUtc, windowStartUtc = since, requestedWindowHours = 72,
				sources = results.ToDictionary(item => item.Key, item => new { item.Value.Status, item.Value.Records, item.Value.Reason, item.Value.Truncated, item.Value.Omitted }),
			}),
			["manager-settings.json"] = DiagnosticBundleWriter.Serialize(settings),
			["deployment.json"] = DiagnosticBundleWriter.Serialize(results["deployment"].Data ?? new { capture.Deployment.Stage }),
			["timeline.jsonl"] = MergeTimeline(timeline.OrderBy(item => item.At).Select(item => item.Row)),
			["lua.json"] = DiagnosticBundleWriter.Serialize(results.Where(item => item.Value.Data is LuaEvidence).ToDictionary(item => item.Key, item => item.Value.Data)),
			["game.json"] = DiagnosticBundleWriter.Serialize(new { nativeReport = results["gameNativeReport"].Data, windowsEvents = crashes }),
		};
		progress.Report("正在保存诊断包…");
		await DiagnosticBundleWriter.WriteAsync(path, entries, cancellationToken).ConfigureAwait(false);
		return gapCount;
	}

	private static byte[] MergeTimeline(IEnumerable<object> rows)
	{
		using var stream = new MemoryStream();
		foreach (var row in rows)
		{
			stream.Write(DiagnosticBundleWriter.Serialize(row));
			stream.WriteByte((byte)'\n');
		}
		return stream.ToArray();
	}

	private static Task<DiagnosticSource> CollectAsync(Func<CancellationToken, Task<DiagnosticSource>> read, CancellationToken cancellationToken)
		=> Task.Run(async () =>
		{
			using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			budget.CancelAfter(TimeSpan.FromSeconds(4));
			try
			{
				var result = await read(budget.Token).ConfigureAwait(false);
				budget.Token.ThrowIfCancellationRequested();
				return result;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new DiagnosticSource("timedOut"); }
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return new DiagnosticSource("missing"); }
			catch (UnauthorizedAccessException) { return new DiagnosticSource("accessDenied"); }
			catch (NotSupportedException) { return new DiagnosticSource("unsupported", Reason: "UnsafeSourcePath"); }
			catch (Exception exception) when (exception is InvalidDataException or JsonException) { return new DiagnosticSource("unsupported", Reason: "UnreadableFormatOrSize"); }
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				return new DiagnosticSource("failed", Reason: DiagnosticJournal.SafeCode(exception.GetType().Name));
			}
		}, cancellationToken);

	private static async Task<DiagnosticSource> ReadEnvironmentAsync(DiagnosticCapture capture, string baseDirectory, CancellationToken token)
	{
		var gameData = capture.Settings.GameData;
		string? build = null;
		string? gameVersion = null;
		string? gameState = null;
		if (!string.IsNullOrWhiteSpace(gameData))
		{
			try
			{
				DiagnosticFiles.GetLocalAttributes(gameData);
				var executable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameData))!, "bin", "helldivers2.exe");
				DiagnosticFiles.GetLocalAttributes(executable);
				gameState = "valid";
				gameVersion = DiagnosticLogParser.NumericVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion);
				var manifestPath = SteamAppManifest.ResolveManifestPath(gameData);
				var (text, _) = await DiagnosticFiles.ReadTextAsync(manifestPath, 64 * 1024, tail: false, token).ConfigureAwait(false);
				build = DiagnosticLogParser.NumericVersion(SteamAppManifest.Parse(text)?.TrustedBuildId);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
			{
				gameState ??= "unavailable";
			}
		}
		var cliVersion = default(string);
		var cliFound = false;
		try
		{
			foreach (var cli in Hd2ModCliLocator.GetCandidatePaths(baseDirectory))
			{
				try { DiagnosticFiles.GetLocalAttributes(cli); }
				catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { continue; }
				cliFound = true;
				cliVersion = DiagnosticLogParser.NumericVersion(FileVersionInfo.GetVersionInfo(cli).FileVersion);
				break;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { }
		var installedLayout = AppUpdateService.Instance?.Package?.Variant;
		return new DiagnosticSource("collected", new
		{
			managerVersion = AppVersion.Current(typeof(App).Assembly), gameBuildId = build, gameVersion, gameDataState = gameState ?? "notSet", cliFound, cliVersion,
			installedLayout = installedLayout is "complete" or "requires-dotnet" or "compat-complete" or "compat-requires-dotnet" ? installedLayout : "developmentOrUnknown",
			roots = new { manager = RootState(baseDirectory), modLibrary = RootState(capture.ModsRoot), gameData = RootState(gameData), export = RootState(capture.Settings.ExportPath, baseDirectory) },
			osVersion = Environment.OSVersion.Version.ToString(), osArchitecture = RuntimeInformation.OSArchitecture,
			processArchitecture = RuntimeInformation.ProcessArchitecture, runtime = Environment.Version.ToString(),
			availableMemoryMiB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
		});
	}

	private static object RootState(string? path, string? relativeTo = null)
	{
		if (string.IsNullOrWhiteSpace(path)) return new { mode = "defaultOrUnset", state = "notSet", freeMiB = (long?)null };
		var mode = Path.IsPathFullyQualified(path) ? "absolute" : "relative";
		try
		{
			var resolved = Path.GetFullPath(relativeTo is not null ? Path.GetFullPath(path, relativeTo) : path);
			var attributes = DiagnosticFiles.GetLocalAttributes(resolved);
			if (!attributes.HasFlag(FileAttributes.Directory)) return new { mode, state = "notDirectory", freeMiB = (long?)null };
			var drive = new DriveInfo(Path.GetPathRoot(resolved)!);
			return new { mode, state = "available", freeMiB = (long?)(drive.AvailableFreeSpace / (1024 * 1024)) };
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return new { mode, state = exception is NotSupportedException ? "skipped" : "unavailable", freeMiB = (long?)null };
		}
	}

	private static async Task<DiagnosticSource> ReadSettingsStateAsync(string path, CancellationToken token)
	{
		var (text, _) = await DiagnosticFiles.ReadTextAsync(path, DiagnosticBundleWriter.MaxSourceBytes, tail: false, token).ConfigureAwait(false);
		using var settings = JsonDocument.Parse(text);
		return settings.RootElement.ValueKind == JsonValueKind.Object
			? new DiagnosticSource("collected") : new DiagnosticSource("unsupported", Reason: "SettingsRootIsNotObject");
	}

	private static Task<DiagnosticSource> ReadDisplayAdaptersAsync(CancellationToken token)
	{
		using var displayClass = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
		if (displayClass is null) return Task.FromResult(new DiagnosticSource("noMatch"));
		var adapters = new HashSet<(string Model, string? Driver)>();
		foreach (var name in displayClass.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)).Take(32))
		{
			token.ThrowIfCancellationRequested();
			using var adapter = displayClass.OpenSubKey(name);
			if (adapter?.GetValue("DriverDesc") is not string model || model.Length is < 4 or > 96
				|| !model.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '(' or ')')
				|| !(model.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) || model.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)
					|| model.StartsWith("ATI ", StringComparison.OrdinalIgnoreCase) || model.StartsWith("Intel", StringComparison.OrdinalIgnoreCase)
					|| model.StartsWith("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase))) continue;
			adapters.Add((model, DiagnosticLogParser.NumericVersion(adapter.GetValue("DriverVersion") as string)));
		}
		return Task.FromResult(new DiagnosticSource(adapters.Count == 0 ? "noMatch" : "collected",
			adapters.Select(item => new { item.Model, driverVersion = item.Driver }).ToArray(), adapters.Count));
	}

	private static async Task<DiagnosticSource> ReadIndexAsync(DiagnosticCapture capture, string root, CancellationToken token)
	{
		var storage = new StoragePaths(root, capture.ModsRoot);
		GameDataIndexStatus? last = capture.LastIndexStatus;
		if (!string.Equals(last?.GameDataDirectory, capture.Settings.GameData, StringComparison.OrdinalIgnoreCase)) last = null;
		var fingerprint = last?.StoredFingerprint;
		if (fingerprint is null)
		{
			DiagnosticFiles.GetLocalAttributes(storage.DbPath);
			fingerprint = await AssetArchiveIndexService.ReadStoredFingerprintAsync(storage.DbPath, token, timeoutSeconds: 2).ConfigureAwait(false);
		}
		if (fingerprint is null && last is null) return new DiagnosticSource("missing");
		return new DiagnosticSource("collected", new
		{
			state = last?.State.ToString() ?? "unknownFreshness", fingerprint?.BuiltUtc, fingerprint?.ArchivesTotal, fingerprint?.ArchivesIndexed, fingerprint?.AssetKeysTotal,
		});
	}

	private static async Task<DiagnosticSource> ReadDeploymentAsync(DiagnosticCapture capture, Aliases aliases, CancellationToken token)
	{
		const int maxEntries = 1400;
		var profile = capture.Library.Profiles.FirstOrDefault(candidate => candidate.Id == capture.Library.ActiveProfileId);
		var members = profile?.Entries.OrderBy(entry => entry.LoadOrder).Take(maxEntries).Select(entry =>
		{
			capture.Library.Nodes.TryGetValue(entry.NodeId, out var node);
			return new { mod = aliases.Mod(entry.NodeId.Value), entry.LoadOrder, kind = node?.Metadata.Kind, patchGroups = node?.PatchGroups.Count };
		}).ToArray();
		var activeOptions = new List<object>();
		var hosts = profile?.Entries.Select(item => item.NodeId.Value).ToHashSet() ?? [];
		var capped = profile?.Entries.Count > maxEntries;
		foreach (var (optionKey, states) in capture.Options.Options)
		{
			token.ThrowIfCancellationRequested();
			if (!Guid.TryParse(optionKey, out var option)) continue;
			foreach (var (hostKey, optionState) in states)
			{
				if (!optionState.Enabled || !Guid.TryParse(hostKey, out var host) || !hosts.Contains(host)) continue;
				if (activeOptions.Count >= maxEntries) { capped = true; break; }
				activeOptions.Add(new { option = aliases.Mod(option), host = aliases.Mod(host), optionState.EffectiveOrder });
			}
			if (activeOptions.Count >= maxEntries) break;
		}
		var deployment = capture.Deployment;
		var desired = new
		{
			active = aliases.Profile(profile?.Id.Value), profile?.Revision, members, activeOptions,
			deployment.Stage, targetProfile = aliases.Profile(deployment.ProfileId?.Value), deployment.TargetRevision,
			issues = deployment.ApplyResult?.Issues.Take(64).Select(issue => ProjectIssue(issue, aliases)).ToArray(),
			failedOperations = deployment.ApplyResult?.Operations.Where(operation => !operation.Success).Take(64).Select(operation => new
			{
				operation.Operation.Kind, code = DiagnosticJournal.SafeCode(operation.ErrorCode), mod = aliases.Mod(operation.Operation.NodeId?.Value),
				archive = ArchiveId(operation.Operation.ArchiveHex16), operation.Operation.TargetPatchIndex, operation.Operation.SidecarKind,
			}).ToArray(),
		};
		if (deployment.Stage is ProfileDeploymentStage.Deploying or ProfileDeploymentStage.Deactivating)
			return new DiagnosticSource("busy", new DeploymentEvidence(desired), Reason: "DeploymentInProgress", Truncated: capped);
		if (string.IsNullOrWhiteSpace(capture.Settings.GameData)) return new DiagnosticSource("missing", new DeploymentEvidence(desired), Reason: "GameDataNotSet", Truncated: capped);
		try
		{
			var file = Path.Combine(capture.Settings.GameData, JsonActivationStateStore.StateFileName);
			var (text, _) = await DiagnosticFiles.ReadTextAsync(file, DiagnosticBundleWriter.MaxSourceBytes, tail: false, token).ConfigureAwait(false);
			var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
			options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
			var state = JsonSerializer.Deserialize<ActivationState>(text, options);
			if (state?.Version != JsonActivationStateStore.CurrentVersion || state.Files is null || state.Issues is null)
				return new DiagnosticSource("unsupported", new DeploymentEvidence(desired), Reason: "ActivationStateFormat", Truncated: capped);
			var committed = new CommittedDeployment(aliases.Profile(state.ProfileId?.Value), state.ProfileRevision, state.AppliedUtc.ToUniversalTime(), state.Completed,
				profile is not null && profile.Id == state.ProfileId && profile.Revision == state.ProfileRevision,
				state.Files.Count, state.ExternalFiles?.Count, state.Files.Where(entry => entry is not null).Take(maxEntries).Select(entry => new
				{
					archive = ArchiveId(entry.ArchiveHex16), entry.SourcePatchIndex, entry.TargetPatchIndex,
					sidecarKind = Enum.IsDefined(entry.SidecarKind) ? entry.SidecarKind.ToString() : "unknown",
					method = Enum.IsDefined(entry.Method) ? entry.Method.ToString() : "unknown", mod = aliases.Mod(entry.NodeId?.Value), entry.Length,
				}).ToArray(), state.Issues.Where(issue => issue is not null).Take(64).Select(issue => ProjectIssue(issue, aliases)).ToArray());
			return new DiagnosticSource("collected", new DeploymentEvidence(desired, committed), state.Files.Count, Truncated: capped || state.Files.Count > maxEntries);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return new DiagnosticSource("missing", new DeploymentEvidence(desired), Reason: "NoActivationState", Truncated: capped); }
		catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
		{
			return new DiagnosticSource("partial", new DeploymentEvidence(desired), Reason: DiagnosticJournal.SafeCode(exception.GetType().Name), Truncated: capped);
		}
	}

	private static object ProjectIssue(CoreIssue issue, Aliases aliases)
		=> new { severity = Enum.IsDefined(issue.Severity) ? issue.Severity.ToString() : "unknown", code = DiagnosticJournal.SafeCode(issue.Code), mod = aliases.Mod(issue.NodeId?.Value) };

	private static string? ArchiveId(string? archive) => archive is { Length: 16 } && archive.All(char.IsAsciiHexDigit) ? archive : null;

	private static string? LocalLuaRoot()
	{
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "CowboyBingus", "Helldivers2");
	}

	private static string? CommonLuaRoot()
	{
		var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		return string.IsNullOrWhiteSpace(common) ? null : Path.Combine(common, "CowboyBingus", "Helldivers2");
	}

	private static async Task<DiagnosticSource> ReadLuaAsync(string? path, DateTimeOffset since, DateTimeOffset until, bool notApplicableWhenMissing, CancellationToken token)
	{
		if (path is null) return new DiagnosticSource(notApplicableWhenMissing ? "notApplicable" : "missing");
		try { DiagnosticFiles.GetLocalAttributes(path); }
		catch (Exception exception) when (notApplicableWhenMissing && exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return new DiagnosticSource("notApplicable", Reason: "ExternalLoaderNotConfigured");
		}
		var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
		if (modified < since || modified > until) return new DiagnosticSource("noMatch", Reason: "ModificationOutsideWindow");
		var isRuntimeStatus = Path.GetFileName(path) == "runtime.status";
		var (text, truncated) = await DiagnosticFiles.ReadTextAsync(path, DiagnosticBundleWriter.MaxSourceBytes, tail: !isRuntimeStatus, token).ConfigureAwait(false);
		if (isRuntimeStatus)
		{
			var status = DiagnosticLogParser.LuaStatus(text);
			return new DiagnosticSource(status.Count == 0 ? "unsupported" : "collected", new LuaEvidence(modified, status, [], new { }), status.Count);
		}
		var facts = DiagnosticLogParser.Lua(text, out int omitted);
		var evidence = new LuaEvidence(modified, null, facts.TakeLast(80).ToArray(), facts.GroupBy(item => item.Code).ToDictionary(group => group.Key, group => group.Count()));
		return new DiagnosticSource(facts.Count > 0 ? "collected" : "noMatch", evidence, facts.Count, "FileTimeOnly", truncated || facts.Count > 80, omitted);
	}

	private static async Task<DiagnosticSource> ReadMdl3LogAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken token)
	{
		var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var path = DiagnosticFiles.SelectNewestExistingPath(
			string.IsNullOrWhiteSpace(common) ? null : Path.Combine(common, "MDL3", "Logs", "MDL3.log"),
			string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "MDL3", "Logs", "MDL3.log"));
		if (path is null) return new DiagnosticSource("notApplicable", Reason: "Mdl3LogNotPresent");
		try { DiagnosticFiles.GetLocalAttributes(path); }
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return new DiagnosticSource("notApplicable", Reason: "Mdl3LogNotPresent");
		}
		var (text, truncated) = await DiagnosticFiles.ReadTextAsync(path, DiagnosticBundleWriter.MaxSourceBytes, tail: true, token).ConfigureAwait(false);
		var items = DiagnosticLogParser.Mdl3(text, since, until, out int omitted);
		return new DiagnosticSource(items.Count == 0 ? "noMatch" : "collected", items, items.Count, Truncated: truncated, Omitted: omitted);
	}

	private static async Task<DiagnosticSource> ReadManagerLogsAsync(string root, string prefix, DateTimeOffset since, DateTimeOffset until, CancellationToken token)
	{
		var log = new List<DiagnosticLogFact>();
		var available = 0;
		var omitted = 0;
		var truncated = false;
		string? reason = null;
		var firstDay = since.LocalDateTime.Date;
		var finalDay = until.LocalDateTime.Date;
		var bytesPerDay = DiagnosticBundleWriter.MaxSourceBytes / (1 + (finalDay - firstDay).Days);
		for (var day = firstDay; day <= finalDay; day = day.AddDays(1))
		{
			token.ThrowIfCancellationRequested();
			var filename = prefix + "_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
			try
			{
				var (text, wasTruncated) = await DiagnosticFiles.ReadTextAsync(Path.Combine(root, filename), bytesPerDay, tail: true, token).ConfigureAwait(false);
				available++;
				truncated |= wasTruncated;
				log.AddRange(DiagnosticLogParser.Manager(text, since, until, out int skipped));
				omitted += skipped;
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				reason ??= DiagnosticJournal.SafeCode(exception.GetType().Name);
			}
		}
		var facts = log.OrderBy(item => item.AtUtc).TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
		return new DiagnosticSource(reason is not null ? "partial" : available == 0 ? "missing" : facts.Length == 0 ? "noMatch" : "collected",
			facts, facts.Length, reason, truncated || facts.Length < log.Count, omitted);
	}

	private static async Task<DiagnosticSource> ReadUpdateLogAsync(string path, DateTimeOffset since, DateTimeOffset until, CancellationToken token)
	{
		var (text, truncated) = await DiagnosticFiles.ReadTextAsync(path, DiagnosticBundleWriter.MaxSourceBytes, tail: true, token).ConfigureAwait(false);
		var items = DiagnosticLogParser.Update(text, since, until, out int omitted);
		return new DiagnosticSource(items.Count == 0 ? "noMatch" : "collected", items, items.Count, Truncated: truncated, Omitted: omitted);
	}

	private static string Summarize(DiagnosticCapture capture, DateTimeOffset since, Dictionary<string, DiagnosticSource> results,
		DiagnosticEvent[] events, List<DiagnosticGameEvidence.CrashEvent> crashes, (string Source, DiagnosticLogFact Fact)[] logs, Aliases aliases)
	{
		var summary = new StringBuilder("HD2 Mod Manager · 故障诊断\n");
		summary.AppendLine($"管理器版本 {AppVersion.Current(typeof(App).Assembly)} · {Format(since)} 至 {Format(capture.CapturedAtUtc)}");
		var profile = capture.Library.Profiles.FirstOrDefault(candidate => candidate.Id == capture.Library.ActiveProfileId);
		summary.AppendLine(profile is null ? "活动配置：无" : $"活动配置：{aliases.Profile(profile.Id.Value)} · revision {profile.Revision} · {profile.Entries.Count} 个 Mod");
		summary.AppendLine($"部署状态：{capture.Deployment.Stage} · 目标 revision {capture.Deployment.TargetRevision} · 详见 deployment.json");
		if (results["deployment"].Data is DeploymentEvidence { Committed: { } committed })
		{
			summary.AppendLine($"磁盘提交：{committed.Profile ?? "无配置"} · revision {committed.ProfileRevision} · {committed.FileCount} 个 Patch 文件 · 完成={committed.Completed}");
			if (profile is not null && !committed.MatchesActiveRevision) summary.AppendLine("活动配置与磁盘提交的版本不一致。详见 deployment.json。");
		}
		var failures = events.Where(item => item.Result == DiagnosticResult.Failed || item.ErrorNumber is not null)
			.Select(item => (item.AtUtc, Text: $"{item.Action} · {item.Code ?? item.Result.ToString()} · timeline.jsonl"))
			.Concat(crashes.Select(item => (item.AtUtc, Text: $"{item.Executable} · Windows {item.EventId} · {item.Module} {item.ExceptionCode} {item.FaultOffset} · game.json")))
			.Concat(logs.Where(item => item.Fact.Level == "ERROR" && item.Fact.AtUtc.HasValue)
				.Select(item => (AtUtc: item.Fact.AtUtc!.Value, Text: $"{item.Fact.Code} · {item.Fact.Exception} · timeline.jsonl")))
			.OrderByDescending(item => item.AtUtc).Take(12).OrderBy(item => item.AtUtc).ToArray();
		summary.AppendLine();
		summary.AppendLine(failures.Length == 0 ? "近期未找到带时间的失败记录。原因未确认。" : "最近的失败记录（时间相邻仅说明先后顺序，根因未确认）");
		foreach (var (at, detail) in failures) summary.AppendLine($"  {Format(at)}  {detail}");
		var last = events.LastOrDefault(item => item.Action != DiagnosticAction.ManagerSession);
		if (last is not null) summary.AppendLine($"最后记录的操作：{Format(last.AtUtc)}  {last.Action} · {last.Result} · timeline.jsonl");
		var incomplete = events.Where(item => item.Action != DiagnosticAction.ManagerSession).GroupBy(item => (item.Session, item.Operation)).Count(group =>
			group.Any(item => item.Result == DiagnosticResult.Started) && group.All(item => item.Result is DiagnosticResult.Started or DiagnosticResult.Waiting or DiagnosticResult.Requested));
		if (incomplete > 0) summary.AppendLine($"{incomplete} 个操作没有结束记录，详见 timeline.jsonl。");
		foreach (var (name, source) in results)
		{
			if (source.Data is not LuaEvidence evidence) continue;
			var error = evidence.Recent.FirstOrDefault(item => item.Code.EndsWith("Error", StringComparison.Ordinal)
				|| item.Code.EndsWith("Failed", StringComparison.Ordinal) || item.Code == "HideDisabledAfterFailures");
			if (error is not null) summary.AppendLine($"Lua：{name} · {error.Code} {error.DetailCode} · 文件修改时间 {Format(evidence.ModifiedAtUtc)} · 事件时间未知 · lua.json");
		}
		if (results["runtime.status"].Data is LuaEvidence { Status: IReadOnlyDictionary<string, object> runtime } status)
		{
			if (runtime.TryGetValue("state", out var state)) summary.AppendLine($"Lua 状态文件：{state} · 文件修改时间 {Format(status.ModifiedAtUtc)} · lua.json");
			if (runtime.TryGetValue("pid", out var pid) && pid is long runtimePid && crashes.Any(item =>
				item.Executable == "helldivers2.exe" && item.ProcessId == runtimePid && item.ProcessStartedUtc is { } started
				&& status.ModifiedAtUtc >= started && status.ModifiedAtUtc <= item.AtUtc))
			{
				summary.AppendLine("Lua 状态文件的 PID 与游戏崩溃记录匹配，且文件修改时间位于该进程的运行时段内。");
			}
		}
		summary.AppendLine();
		summary.AppendLine("采集范围与缺口（详见 manifest.json）");
		foreach (var (name, source) in results)
			summary.AppendLine($"  {name} · {source.Status} · {source.Records} 条{(source.Truncated ? " · 已截断" : "")}{(source.Reason is null ? "" : " · " + source.Reason)}");
		return summary.ToString();
	}

	private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
