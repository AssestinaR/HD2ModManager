using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 一个 Mod 的「按实例隐藏」记录（规划 §6.3）。P1 由人手写（或 P3 的转专属生成）；
/// 部署时只把<b>启用中</b>的 Mod 的记录编译成 <c>kit-instance.rules</c>。
/// </summary>
/// <param name="TargetItemId">目标套的 item_id，仅供界面与日志。</param>
/// <param name="ContentFingerprint">
/// 生成这条记录时 Mod 内容的指纹。<c>null</c> 或空串 = 不校验（P1 允许）；
/// 非空时，若调用方给出的当前指纹与它不一致，这条记录不生效（见 <see cref="KitInstanceRulesCompiler"/>）。
/// </param>
public sealed record KitInstanceWorkspaceRecord(
	uint TargetItemId,
	string RuleName,
	double Radius,
	IReadOnlyList<string> All,
	IReadOnlyList<string> None,
	IReadOnlyList<string> Hide,
	string? ContentFingerprint = null)
{
	public KitInstanceRule ToRule()
		=> new(RuleName, Radius, All ?? [], None ?? [], Hide ?? []);
}

/// <summary>workspace 记录集合，按 <see cref="ModNodeId"/> 索引。</summary>
public sealed record KitInstanceWorkspace(IReadOnlyDictionary<ModNodeId, KitInstanceWorkspaceRecord> Records)
{
	public const int SchemaVersion = 1;
	public static KitInstanceWorkspace Empty { get; } = new(new Dictionary<ModNodeId, KitInstanceWorkspaceRecord>());

	public KitInstanceWorkspace With(ModNodeId nodeId, KitInstanceWorkspaceRecord record)
	{
		var copy = Records.ToDictionary(pair => pair.Key, pair => pair.Value);
		copy[nodeId] = record;
		return new KitInstanceWorkspace(copy);
	}

	public KitInstanceWorkspace Without(ModNodeId nodeId)
	{
		var copy = Records.ToDictionary(pair => pair.Key, pair => pair.Value);
		copy.Remove(nodeId);
		return new KitInstanceWorkspace(copy);
	}
}

/// <summary>
/// <c>&lt;mods&gt;/.hd2modmanager/kit-instance.workspace.json</c> 的读写。
/// 读：文件不存在 = 空；内容损坏或 schema 不对抛 <see cref="InvalidDataException"/>（调用方决定怎么报）。
/// 写：临时文件 + 原子替换，与 <c>wardrobe.links</c> 同一约定。
/// </summary>
public static class KitInstanceWorkspaceStore
{
	public const string FileName = "kit-instance.workspace.json";
	private const long MaximumBytes = 4 * 1024 * 1024;

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static string GetPath(string modsRootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		return Path.Combine(Path.GetFullPath(modsRootDirectory), ".hd2modmanager", FileName);
	}

	public static async ValueTask<KitInstanceWorkspace> ReadAsync(string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		var path = GetPath(modsRootDirectory);
		if (!File.Exists(path)) return KitInstanceWorkspace.Empty;
		if (new FileInfo(path).Length > MaximumBytes)
			throw new InvalidDataException("kit-instance.workspace.json 过大。");
		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
		}
		catch (FileNotFoundException) { return KitInstanceWorkspace.Empty; }
		return Parse(json);
	}

	public static KitInstanceWorkspace Parse(string json)
	{
		WorkspaceFile? file;
		try { file = JsonSerializer.Deserialize<WorkspaceFile>(json, Options); }
		catch (JsonException exception) { throw new InvalidDataException("kit-instance.workspace.json 不是合法 JSON：" + exception.Message, exception); }
		if (file is null) throw new InvalidDataException("kit-instance.workspace.json 为空。");
		if (file.Schema != KitInstanceWorkspace.SchemaVersion)
			throw new InvalidDataException($"kit-instance.workspace.json 的 schema {file.Schema} 不受支持。");
		var records = new Dictionary<ModNodeId, KitInstanceWorkspaceRecord>();
		foreach (var (key, value) in file.Records ?? [])
		{
			if (!Guid.TryParse(key, out var guid))
				throw new InvalidDataException($"kit-instance.workspace.json 的键不是 Mod 节点 ID：{key}");
			if (value is null) throw new InvalidDataException($"记录 {key} 为空。");
			records[new ModNodeId(guid)] = new KitInstanceWorkspaceRecord(
				value.TargetItemId,
				value.RuleName ?? string.Empty,
				value.Radius,
				value.All ?? [],
				value.None ?? [],
				value.Hide ?? [],
				string.IsNullOrEmpty(value.ContentFingerprint) ? null : value.ContentFingerprint);
		}
		return new KitInstanceWorkspace(records);
	}

	public static string Serialize(KitInstanceWorkspace workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var file = new WorkspaceFile
		{
			Schema = KitInstanceWorkspace.SchemaVersion,
			Records = workspace.Records
				.OrderBy(pair => pair.Key.Value)
				.ToDictionary(
					pair => pair.Key.Value.ToString("D"),
					pair => new RecordFile
					{
						TargetItemId = pair.Value.TargetItemId,
						RuleName = pair.Value.RuleName,
						Radius = pair.Value.Radius,
						All = pair.Value.All.ToArray(),
						None = pair.Value.None.ToArray(),
						Hide = pair.Value.Hide.ToArray(),
						ContentFingerprint = pair.Value.ContentFingerprint,
					}),
		};
		return JsonSerializer.Serialize(file, Options);
	}

	public static async ValueTask WriteAsync(
		string modsRootDirectory, KitInstanceWorkspace workspace, CancellationToken cancellationToken = default)
	{
		var path = GetPath(modsRootDirectory);
		await KitInstanceFiles.WriteAtomicTextAsync(path, Serialize(workspace), cancellationToken).ConfigureAwait(false);
	}

	private sealed class WorkspaceFile
	{
		public int Schema { get; set; }
		public Dictionary<string, RecordFile>? Records { get; set; }
	}

	private sealed class RecordFile
	{
		public uint TargetItemId { get; set; }
		public string? RuleName { get; set; }
		public double Radius { get; set; }
		public string[]? All { get; set; }
		public string[]? None { get; set; }
		public string[]? Hide { get; set; }
		public string? ContentFingerprint { get; set; }
	}
}

internal static class KitInstanceFiles
{
	/// <summary>临时文件 + <c>File.Move(overwrite)</c>：读的一侧只会看到旧版或新版，看不到半份。</summary>
	public static async Task WriteAtomicTextAsync(string path, string text, CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(
				temporaryPath,
				text,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}
}
