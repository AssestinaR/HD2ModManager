using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HD2ModCore.Infrastructure.Diagnostics;

namespace HD2ModManager.Services;

internal static class DiagnosticGameEvidence
{
	internal sealed record CrashEvent(DateTimeOffset AtUtc, int EventId, string Executable, string? Version,
		string? Module, string? ModuleVersion, string? ExceptionCode, string? FaultOffset, uint? ProcessId, DateTimeOffset? ProcessStartedUtc);

	internal static Task<DiagnosticSource> ReadWindowsEventsAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
	{
		var start = since.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
		var end = until.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
		var query = new EventLogQuery("Application", PathType.LogName,
			$"*[System[TimeCreated[@SystemTime >= '{start}' and @SystemTime <= '{end}'] and " +
			"((Provider[@Name='Application Error'] and EventID=1000) or " +
			"(Provider[@Name='Windows Error Reporting'] and EventID=1001) or " +
			"(Provider[@Name='Application Hang'] and EventID=1002))]]")
		{
			ReverseDirection = true,
		};
		using var reader = new EventLogReader(query);
		var events = new List<CrashEvent>();
		const int recordLimit = 500;
		var scanned = 0;
		while (scanned < recordLimit)
		{
			cancellationToken.ThrowIfCancellationRequested();
			using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(300));
			if (record is null) break;
			scanned++;
			if (record.TimeCreated is not { } time) continue;
			var xml = record.ToXml();
			if (xml.Length > 64 * 1024) continue;
			var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var document = XElement.Parse(xml);
			foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Data"))
			{
				if ((string?)element.Attribute("Name") is { } name) fields.TryAdd(name, element.Value);
			}
			var crash = ProjectEvent(new DateTimeOffset(time).ToUniversalTime(), record.Id, fields);
			if (crash is not null) events.Add(crash);
		}
		var status = events.Count == 0 ? "noMatch" : "collected";
		return Task.FromResult(new DiagnosticSource(status, events, events.Count, Truncated: scanned == recordLimit));
	}

	private static CrashEvent? ProjectEvent(DateTimeOffset at, int id, Dictionary<string, string> fields)
	{
		var executable = fields.GetValueOrDefault(id == 1001 ? "P1" : "AppName");
		if (string.Equals(executable, "helldivers2.exe", StringComparison.OrdinalIgnoreCase)) executable = "helldivers2.exe";
		else if (string.Equals(executable, "HD2ModManager.exe", StringComparison.OrdinalIgnoreCase)) executable = "HD2ModManager.exe";
		else return null;

		if (id == 1001 && fields.GetValueOrDefault("EventName") is not ("APPCRASH" or "BEX64" or "AppHangB1" or "AppHangXProcB1")) return null;
		var processId = Hex(fields.GetValueOrDefault("ProcessId"), 8);
		var appTime = Hex(fields.GetValueOrDefault("ProcessCreationTime"), 16);
		var module = fields.GetValueOrDefault(id == 1001 ? "P4" : "ModuleName");
		if (module is not { Length: > 4 and <= 72 } || !module.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
			|| !(module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) module = null;
		if (id == 1001 && fields.GetValueOrDefault("EventName") is "AppHangB1" or "AppHangXProcB1") module = null;
		return new CrashEvent(at, id, executable,
			DiagnosticLogParser.NumericVersion(fields.GetValueOrDefault(id == 1001 ? "P2" : "AppVersion")),
			module, module is null ? null : DiagnosticLogParser.NumericVersion(fields.GetValueOrDefault(id == 1001 ? "P5" : "ModuleVersion")),
			Hex(fields.GetValueOrDefault(id == 1001 && fields.GetValueOrDefault("EventName") == "APPCRASH" ? "P7" : "ExceptionCode"), 8),
			Hex(fields.GetValueOrDefault(id == 1001 && fields.GetValueOrDefault("EventName") == "APPCRASH" ? "P8" : "FaultingOffset"), 16),
			processId is null ? null : uint.Parse(processId.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
			appTime is not null && long.TryParse(appTime.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime)
				&& fileTime is > 0 and <= 2650467743999999999L ? DateTimeOffset.FromFileTime(fileTime).ToUniversalTime() : null);
	}

	private static string? Hex(string? value, int limit)
	{
		if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true) value = value[2..];
		return value is { Length: > 0 } && value.Length <= limit && value.All(char.IsAsciiHexDigit) ? "0x" + value.ToUpperInvariant() : null;
	}

	internal static Task<DiagnosticSource> ReadNativeReportsAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
	{
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (string.IsNullOrWhiteSpace(roaming)) return Task.FromResult(new DiagnosticSource("missing", Reason: "RoamingFolderUnavailable"));
		var root = Path.Combine(roaming, "Arrowhead", "Helldivers2", "crash_data");
		DiagnosticFiles.GetLocalAttributes(root);
		var dumps = new List<object>();
		var unknownFormats = 0;
		var capped = false;
		var scanned = 0;
		const int maxEntries = 256;
		Scan(root, includeChildren: true);
		return Task.FromResult(new DiagnosticSource(dumps.Count == 0 && unknownFormats == 0 ? "noMatch" : "unsupported",
			new { dumps, unknownFormats }, dumps.Count, unknownFormats == 0 ? "NoSupportedTextReport" : "UnrecognizedNativeReportFormat", capped));

		void Scan(string directory, bool includeChildren)
		{
			foreach (var path in Directory.EnumerateFileSystemEntries(directory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (scanned++ >= maxEntries)
				{
					capped = true;
					return;
				}
				var attributes = File.GetAttributes(path);
				if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
				if (attributes.HasFlag(FileAttributes.Directory))
				{
					if (includeChildren) Scan(path, includeChildren: false);
					continue;
				}
				var file = new FileInfo(path);
				if (file.LastWriteTimeUtc < since.UtcDateTime || file.LastWriteTimeUtc > until.UtcDateTime) continue;
				var extension = file.Extension.ToLowerInvariant();
				if (extension is ".dmp" or ".mdmp") dumps.Add(new { type = extension, bytes = file.Length, modifiedAtUtc = file.LastWriteTimeUtc });
				else unknownFormats++;
			}
		}
	}
}
