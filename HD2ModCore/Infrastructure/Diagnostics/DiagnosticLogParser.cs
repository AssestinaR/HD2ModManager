using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HD2ModCore.Infrastructure.Diagnostics;

public sealed record DiagnosticLogFact(DateTimeOffset? AtUtc, string Code, string? Level = null,
	string? Exception = null, int? ExitCode = null, long? Revision = null, IReadOnlyList<string>? Frames = null,
	string? DetailCode = null, int? LineNumber = null, double? RuntimeSeconds = null, string? RuntimeVersion = null);

public static partial class DiagnosticLogParser
{
	private static readonly (string Prefix, string Code)[] ManagerMessages =
	[
		("Unhandled UI exception:", "UnhandledUiException"), ("Unhandled process exception:", "UnhandledProcessException"),
		("快速导入完成：", "ImportCompleted"),
		("导入没有产生 Mod：", "ImportNoMod"), ("导入失败", "ImportFailed"),
		("更新已应用：", "UpdateApplied"), ("更新 ", "UpdateResult"),
		("命令行程序未找到", "CliMissing"), ("命令行程序：", "CliLocated"), ("启动性能：", "StartupStage"),
		("部署：", "DeploymentIssue"), ("UIPerf ", "UiPerformance"),
	];

	private static readonly (string Prefix, string Code)[] LuaMessages =
	[
		("deployment_mode=", "Startup"), ("runtime loaded;", "Startup"), ("ready:", "Ready"), ("waiting:", "Waiting"),
		("located:", "Located"), ("protocol_error", "ProtocolError"), ("locator_error", "LocatorError"),
		("apply_error", "ApplyError"), ("partial_apply", "PartialApply"), ("skipped_target:", "SkippedTarget"),
		("game_fingerprint_failed", "GameFingerprintFailed"), ("update_unavailable", "UpdateUnavailable"),
		("read failed:", "ReadFailed"), ("parse failed,", "ParseFailed"), ("drain error:", "DrainError"),
		("poll error:", "PollError"), ("section mask rollback FAILED", "RollbackFailed"),
		("install: start", "BankPatchStarted"), ("install: load_kernel failed", "BankPatchKernelFailed"),
		("install: module_base failed", "BankPatchModuleFailed"), ("entry: installed ok", "BankPatchInstalled"),
		("entry: install failed", "BankPatchInstallFailed"), ("weight_audit_error:", "WeightAuditError"),
		("section_mask_unit_missing", "SectionMaskUnitMissing"), ("section_mask_plan_failed", "SectionMaskPlanFailed"),
	];
	private static readonly (string Text, string Code)[] LuaErrorDetails =
	[
		("nil value", "NilValue"), ("stack overflow", "StackOverflow"), ("bad argument", "BadArgument"),
		("attempt to index", "InvalidIndex"), ("attempt to call", "InvalidCall"), ("not found", "NotFound"),
		("permission denied", "PermissionDenied"), ("schema", "Schema"), ("checksum", "Checksum"),
	];

	private static readonly (string AddonPrefix, string CodePrefix)[] KnownBootAddons =
	[
		("mods/codex/runtime_armor_registry: ", "ArmorAddon"),
		("mods/hd2modmanager/kit_instance_hider: ", "KitHiderAddon"),
		("mods/hd2modmanager/physbone: ", "PhysboneAddon"),
		("mods/codex/hd2_data_hub: ", "DataHubAddon"),
	];

	public static IReadOnlyList<DiagnosticLogFact> Manager(string text, DateTimeOffset since, DateTimeOffset until, out int omitted)
	{
		var facts = new List<DiagnosticLogFact>();
		var omittedCount = 0;
		var lines = text.Split('\n');
		for (var index = 0; index < lines.Length; ++index)
		{
			var line = lines[index].AsSpan().Trim();
			if (line.IsEmpty) continue;
			var end = line.IndexOf(']');
			if (line[0] != '[' || end < 2 || !DateTimeOffset.TryParseExact(line[1..end], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
			{
				omittedCount++;
				continue;
			}
			if (time < since || time > until) continue;
			var body = line[(end + 1)..].TrimStart();
			var space = body.IndexOf(' ');
			if (space < 0 || body[..space].ToString() is not ("ERROR" or "WARN" or "INFO" or "TRACE"))
			{
				omittedCount++;
				continue;
			}
			var level = body[..space].ToString();
			var message = body[(space + 1)..].ToString();
			var kind = ManagerMessages.FirstOrDefault(item => message.StartsWith(item.Prefix, StringComparison.Ordinal)).Code;
			var exception = ExceptionName().Match(message);
			var exitCode = CliExitCode().Match(message);
			var frames = new List<string>();
			if (level is "ERROR" or "WARN")
			{
				while (index + 1 < lines.Length && !lines[index + 1].StartsWith('['))
				{
					var next = lines[++index].Trim();
					var frame = FrameName().Match(next);
					if (frame.Success && frames.Count < 12) frames.Add(frame.Groups[1].Value);
					else if (next.Length != 0) omittedCount++;
				}
			}
			if (kind is null && !exception.Success && !exitCode.Success)
			{
				omittedCount++;
				if (level is not ("ERROR" or "WARN")) continue;
			}
			facts.Add(new DiagnosticLogFact(time.ToUniversalTime(), kind ?? "ManagerLogEntry", level,
				exception.Success ? exception.Groups[1].Value : null,
				exitCode.Success && int.TryParse(exitCode.Groups[1].Value, CultureInfo.InvariantCulture, out int result) ? result : null,
				Frames: frames.Count == 0 ? null : frames));
		}
		omitted = omittedCount + Math.Max(0, facts.Count - DiagnosticBundleWriter.MaxEvents);
		return facts.TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
	}

	public static IReadOnlyList<DiagnosticLogFact> Lua(string text, out int omitted)
	{
		var facts = new List<DiagnosticLogFact>();
		omitted = 0;
		foreach (var value in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = value.Trim();
			if (line.StartsWith('{') && ReadKitHiderEvent(line) is { } kitEvent)
			{
				facts.Add(kitEvent);
				continue;
			}
			if (ReadMdlBootEvent(line) is { } bootEvent)
			{
				facts.Add(bootEvent);
				continue;
			}
			var kind = LuaMessages.FirstOrDefault(item => line.StartsWith(item.Prefix, StringComparison.Ordinal)).Code;
			if (kind is null)
			{
				omitted++;
				continue;
			}
			var revision = Revision().Match(line);
			var versionMatch = InlineVersion().Match(line);
			var detail = LuaErrorDetails.FirstOrDefault(item => line.Contains(item.Text, StringComparison.OrdinalIgnoreCase)).Code;
			var location = LuaLineNumber().Match(line);
			facts.Add(new DiagnosticLogFact(null, kind, Revision: revision.Success && long.TryParse(revision.Groups[1].Value, CultureInfo.InvariantCulture, out long number) ? number : null,
				DetailCode: detail, LineNumber: location.Success && int.TryParse(location.Groups[1].Value, CultureInfo.InvariantCulture, out int lineNumber) ? lineNumber : null,
				RuntimeVersion: versionMatch.Success ? NumericVersion(versionMatch.Groups[1].Value) : null));
		}
		omitted += Math.Max(0, facts.Count - DiagnosticBundleWriter.MaxEvents);
		return facts.TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
	}

	private static DiagnosticLogFact? ReadKitHiderEvent(string line)
	{
		if (line.Length > 4096) return null;
		try
		{
			using var document = JsonDocument.Parse(line);
			var row = document.RootElement;
			if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
			var code = kind.GetString() switch
			{
				"start" => "Startup", "rules" => "RulesLoaded", "rules_error" => "RulesError",
				"hide_disabled" => "HideDisabledAfterFailures", "tick_error" => "TickError", _ => null,
			};
			if (code is null) return null;
			var version = row.TryGetProperty("version", out var build) && build.ValueKind == JsonValueKind.String ? NumericVersion(build.GetString()) : null;
			var seconds = row.TryGetProperty("t", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetDouble(out double value)
				&& double.IsFinite(value) && value >= 0 ? value : (double?)null;
			return new DiagnosticLogFact(null, code, RuntimeSeconds: seconds, RuntimeVersion: version);
		}
		catch (JsonException) { return null; }
	}

	public static IReadOnlyList<DiagnosticLogFact> Update(string text, DateTimeOffset since, DateTimeOffset until, out int omitted)
	{
		var facts = new List<DiagnosticLogFact>();
		omitted = 0;
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			if (line.Length < 20 || !DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
				|| TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
			{
				omitted++;
				continue;
			}
			var time = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
			if (time < since || time > until) continue;
			var message = line[20..];
			if (!message.StartsWith("update ", StringComparison.Ordinal)) { omitted++; continue; }
			var result = message.IndexOf(' ', 7);
			if (result < 0) { omitted++; continue; }
			var stage = message[(result + 1)..];
			var kind = stage.StartsWith("applied (", StringComparison.Ordinal) ? "UpdateApplied"
				: stage.StartsWith("failed:", StringComparison.Ordinal) ? "UpdateFailed"
				: stage.StartsWith("waiting for process ", StringComparison.Ordinal) ? "UpdateWaiting" : null;
			if (kind is null) { omitted++; continue; }
			facts.Add(new DiagnosticLogFact(time, kind, kind == "UpdateFailed" ? "ERROR" : "INFO"));
		}
		omitted += Math.Max(0, facts.Count - DiagnosticBundleWriter.MaxEvents);
		return facts.TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
	}

	public static IReadOnlyDictionary<string, object> LuaStatus(string text)
	{
		var facts = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = line.IndexOf('=');
			if (separator <= 0) continue;
			var key = line[..separator];
			var value = line[(separator + 1)..].Trim();
			if (key is "schema" or "pid" or "revision" or "active_mappings" or "game_timestamp" or "game_image_size" or "record_count"
				&& long.TryParse(value, CultureInfo.InvariantCulture, out long number)) facts[key] = number;
			else if ((key == "state" && value is "ready" or "waiting" or "located" or "apply_error" or "protocol_error" or "locator_error")
				|| (key == "deployment_mode" && value is "shared_loader" or "direct")
				|| (key == "reload_trigger" && value is "auto_refresh" or "passive")
				|| (key == "runtime_version" && VersionValue().IsMatch(value))) facts[key] = value;
		}
		return facts;
	}

	private static DiagnosticLogFact? ReadMdlBootEvent(string line)
	{
		if (line.StartsWith("MDL3 startup coordinator;", StringComparison.Ordinal))
			return new DiagnosticLogFact(null, "Mdl3Coordinator");
		if (line.StartsWith("SAFE MODE (", StringComparison.Ordinal))
		{
			var mode = line.Contains("(config)", StringComparison.Ordinal) ? "Config"
				: line.Contains("(manager)", StringComparison.Ordinal) ? "Manager" : null;
			return new DiagnosticLogFact(null, "MdlSafeMode", DetailCode: mode);
		}
		if (line.StartsWith("Discovery: ", StringComparison.Ordinal))
		{
			if (line.StartsWith("Discovery: failed:", StringComparison.Ordinal))
				return new DiagnosticLogFact(null, "MdlDiscoveryFailed");
			if (line is "Discovery: waiting for the game folder" or "Discovery: game folder unknown")
				return new DiagnosticLogFact(null, "MdlDiscoveryNoGameFolder");
			var discovery = MdlDiscovery().Match(line);
			if (discovery.Success && long.TryParse(discovery.Groups[2].Value, CultureInfo.InvariantCulture, out var archiveCount))
			{
				var detail = line.Contains("; ", StringComparison.Ordinal) ? "PartialScanErrors" : null;
				return new DiagnosticLogFact(null, "MdlDiscovery", Revision: archiveCount, DetailCode: detail);
			}
			return null;
		}
		foreach (var (addonPrefix, codePrefix) in KnownBootAddons)
		{
			if (!line.StartsWith(addonPrefix, StringComparison.Ordinal)) continue;
			var status = line[addonPrefix.Length..];
			var suffix = status switch
			{
				"managed" => "Managed",
				"loaded" => "Loaded",
				"loading" => "Loading",
				"not installed" => "NotInstalled",
				"skipped: safe mode" => "SkippedSafeMode",
				_ when status.StartsWith("disabled by user", StringComparison.Ordinal) => "DisabledByUser",
				_ when status.StartsWith("lookup failed", StringComparison.Ordinal) => "LookupFailed",
				_ when status.StartsWith("load failed", StringComparison.Ordinal) => "LoadFailed",
				_ => null,
			};
			if (suffix is null) return null;
			var detail = suffix.EndsWith("Failed", StringComparison.Ordinal)
				? LuaErrorDetails.FirstOrDefault(item => status.Contains(item.Text, StringComparison.OrdinalIgnoreCase)).Code
				: null;
			var location = suffix == "LoadFailed" ? LuaLineNumber().Match(status) : Match.Empty;
			return new DiagnosticLogFact(
				null,
				codePrefix + suffix,
				DetailCode: detail,
				LineNumber: location.Success && int.TryParse(location.Groups[1].Value, CultureInfo.InvariantCulture, out var lineNo) ? lineNo : null);
		}
		return null;
	}

	public static IReadOnlyList<DiagnosticLogFact> Mdl3(string text, DateTimeOffset since, DateTimeOffset until, out int omitted)
	{
		var facts = new List<DiagnosticLogFact>();
		omitted = 0;
		foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length < 21 || line[19] != ' '
				|| !DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
				|| TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
			{
				omitted++;
				continue;
			}
			var time = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
			if (time < since || time > until) continue;
			var message = line[20..].Trim();
			DiagnosticLogFact? fact = null;
			if (message.StartsWith("BOOT version=", StringComparison.Ordinal))
			{
				var rest = message["BOOT version=".Length..];
				var space = rest.IndexOf(' ');
				var rawVersion = space < 0 ? rest : rest[..space];
				fact = new DiagnosticLogFact(time, "Mdl3Boot", "INFO", RuntimeVersion: Mdl3VersionValue().IsMatch(rawVersion) ? rawVersion : null);
			}
			else if (message.StartsWith("DATA_ROOT ", StringComparison.Ordinal))
			{
				var rootKind = message.EndsWith("(older install in the user folder)", StringComparison.Ordinal) ? "LocalAppData"
					: message.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) ? "ProgramData" : "Custom";
				fact = new DiagnosticLogFact(time, "Mdl3DataRoot", "INFO", DetailCode: rootKind);
			}
			else if (message.StartsWith("START_FAILED Run MDL3Setup.exe first", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3SetupMissingFailed", "ERROR");
			else if (message.StartsWith("START_FAILED ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3StartFailed", "ERROR");
			else if (message.StartsWith("CONFIG_DLL_NON_ASCII ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3ConfigDllNonAscii", "INFO");
			else if (message == "MDL_RUNTIME_BOOT ok")
				fact = new DiagnosticLogFact(time, "Mdl3RuntimeBootOk", "INFO");
			else if (message.StartsWith("MDL_RUNTIME_FAILED ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3RuntimeBootFailed", "ERROR");
			else if (message.StartsWith("BRIDGE_READY ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3BridgeReady", "INFO");
			else if (message.StartsWith("SYS_READY ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3SysReady", "INFO", DetailCode: message.Contains("(no watcher)", StringComparison.Ordinal) ? "NoWatcher" : null);
			else if (message.StartsWith("SYS_FAILED ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3SysFailed", "ERROR");
			else if (message.StartsWith("SYS_WATCH_UNAVAILABLE ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3WatchUnavailable", "WARN");
			else if (message.StartsWith("DIAG_MODE ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3DiagMode", "WARN");
			else if (message.StartsWith("MDL_UPDATE_FAILED ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3UpdateFailed", "ERROR");
			else if (message.StartsWith("GAME_SHUTDOWN ", StringComparison.Ordinal))
				fact = new DiagnosticLogFact(time, "Mdl3Shutdown", "INFO");

			if (fact is null)
			{
				omitted++;
				continue;
			}
			facts.Add(fact);
		}
		omitted += Math.Max(0, facts.Count - DiagnosticBundleWriter.MaxEvents);
		return facts.TakeLast(DiagnosticBundleWriter.MaxEvents).ToArray();
	}

	public static string? NumericVersion(string? value) => value is not null && VersionValue().IsMatch(value) ? value : null;

	[GeneratedRegex(@"^Discovery: ([0-9]{1,6}) declared entries in ([0-9]{1,6}) archive\(s\)", RegexOptions.CultureInvariant)]
	private static partial Regex MdlDiscovery();
	[GeneratedRegex(@"\A[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:-[A-Za-z0-9.]{1,24})?\z", RegexOptions.CultureInvariant)]
	private static partial Regex Mdl3VersionValue();

	[GeneratedRegex(@"\b((?:System\.|Microsoft\.|HD2Mod(?:Core|Manager|Adaptation)\.)[A-Za-z0-9_.]{0,95}Exception)(?=[:\s]|$)", RegexOptions.CultureInvariant)]
	private static partial Regex ExceptionName();
	[GeneratedRegex(@"^(?:at|在) ((?:System|Microsoft|HD2ModCore|HD2ModManager|HD2ModAdaptation)\.[A-Za-z0-9_.<>`]{1,140})\(", RegexOptions.CultureInvariant)]
	private static partial Regex FrameName();
	[GeneratedRegex(@"hd2mod 退出码 (-?[0-9]{1,10})(?![0-9])", RegexOptions.CultureInvariant)]
	private static partial Regex CliExitCode();
	[GeneratedRegex(@"\brevision=(-?[0-9]{1,19})(?![0-9])", RegexOptions.CultureInvariant)]
	private static partial Regex Revision();
	[GeneratedRegex(@"\bversion=([0-9]{1,10}(?:\.[0-9]{1,10}){1,4})\b", RegexOptions.CultureInvariant)]
	private static partial Regex InlineVersion();
	[GeneratedRegex(@"\.lua:([0-9]{1,7}):", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
	private static partial Regex LuaLineNumber();
	[GeneratedRegex(@"\A[0-9]{1,10}(?:\.[0-9]{1,10}){0,4}\z", RegexOptions.CultureInvariant)]
	private static partial Regex VersionValue();
}
