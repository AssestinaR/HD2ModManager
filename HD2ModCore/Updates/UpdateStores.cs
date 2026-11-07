using System.Text.Json;

namespace HD2ModCore.Updates;

/// <summary>更新偏好：存在 config\app-update.json。损坏时回到默认值，不阻止程序启动。</summary>
public sealed record AppUpdatePrefs
{
	public int Version { get; init; } = 1;
	public bool AutoCheck { get; init; } = true;
	/// <summary>发现新版本后自动下载并（程序空闲时）自动安装。默认关：管理器可能正在部署，不应擅自重启。</summary>
	public bool AutoUpdate { get; init; }
	public string? Skipped { get; init; }
	public DownloadSourceMode DownloadSource { get; init; } = DownloadSourceMode.Ask;
	public string MirrorId { get; init; } = string.Empty;
	public string CustomMirror { get; init; } = string.Empty;

	public UpdateNetworkSettings Network => new(DownloadSource, MirrorId, CustomMirror);

	internal AppUpdatePrefs Sanitized() => this with
	{
		Skipped = AppVersion.Normalize(Skipped),
		DownloadSource = Enum.IsDefined(DownloadSource) ? DownloadSource : DownloadSourceMode.Ask,
		MirrorId = UpdateMirrors.All.Any(m => m.Id == MirrorId) || MirrorId == UpdateMirrors.CustomId ? MirrorId : string.Empty,
		CustomMirror = UpdateMirrors.NormalizeCustom(CustomMirror) ?? string.Empty,
	};
}

/// <summary>小 JSON 文件的原子读写（写临时文件再替换）。</summary>
internal sealed class JsonFileStore<T> where T : class
{
	private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
	private readonly string _path;
	private readonly object _gate = new();

	public JsonFileStore(string path) => _path = path;

	public T? Load()
	{
		lock (_gate)
		{
			try { return File.Exists(_path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) : null; }
			catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
		}
	}

	public bool Save(T value)
	{
		lock (_gate)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
				var tmp = _path + ".tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
				File.Move(tmp, _path, true);
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
		}
	}
}

internal sealed record CheckRecord(DateTimeOffset At, string? Current, List<GithubRelease>? List, UpdateErrorCode? Error, DigestSource? DigestSource);

/// <summary>自动检查的节流（每天一次）：连不上、被中断也算一次尝试。</summary>
internal sealed class UpdateCheckStore
{
	private readonly JsonFileStore<CheckRecord> _store;
	private CheckRecord? _value;

	public UpdateCheckStore(string configDir)
	{
		_store = new JsonFileStore<CheckRecord>(Path.Combine(configDir, "update-checks.json"));
		_value = _store.Load();
	}

	public CheckRecord? Get() => _value;

	/// <summary>距下次允许自动检查还要多久；时钟被调回时不能让检查被无限期压住。</summary>
	public TimeSpan Remaining(DateTimeOffset now, TimeSpan interval)
	{
		if (_value is null || now < _value.At) return TimeSpan.Zero;
		var left = interval - (now - _value.At);
		return left < TimeSpan.Zero ? TimeSpan.Zero : left;
	}

	public void Save(CheckRecord record)
	{
		_value = record;
		_store.Save(record);
	}
}
