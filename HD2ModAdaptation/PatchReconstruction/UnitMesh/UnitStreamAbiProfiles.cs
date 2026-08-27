namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Resolves a Unit stream ABI from either a known version family or a
// strictly validated structural fingerprint. Version numbers are useful hints,
// but the actual component table and vertex strides remain the authority.
public enum UnitStreamAbiResolutionPath
{
	ExactVersionProfile = 0,
	FingerprintFallback = 1,
	Unresolved = 2
}

public enum UnitStreamAbiProfileKind
{
	Unknown = 0,
	LegacyPre10800438 = 1,
	Current10800438 = 2
}

public sealed record UnitStreamAbiResolution(
	UnitStreamAbiResolutionPath Path,
	UnitStreamAbiProfileKind Profile,
	uint UnitVersion,
	IReadOnlyList<string> Evidence)
{
	public static UnitStreamAbiResolution Synthetic { get; } = new(
		UnitStreamAbiResolutionPath.ExactVersionProfile,
		UnitStreamAbiProfileKind.Current10800438,
		0,
		["由测试或调用方直接构造的 Unit 模型。"]);

	public bool IsResolved => Path != UnitStreamAbiResolutionPath.Unresolved
		&& Profile != UnitStreamAbiProfileKind.Unknown;

	public bool UsedFallback => Path == UnitStreamAbiResolutionPath.FingerprintFallback;

	public string Describe()
	{
		var path = Path switch
		{
			UnitStreamAbiResolutionPath.ExactVersionProfile => "ExactVersionProfile",
			UnitStreamAbiResolutionPath.FingerprintFallback => "FingerprintFallback",
			_ => "Unresolved"
		};
		return $"{path}/{Profile}";
	}
}

public sealed record UnitStreamAbiRawComponent(uint Type, uint Format, uint Index, ulong Unknown);

public sealed record UnitStreamAbiRawStream(
	int Index,
	uint VertexStride,
	IReadOnlyList<UnitStreamAbiRawComponent> Components);

public readonly record struct UnitStreamAbiFormat(string Name, uint Size);

public static class UnitStreamAbiProfiles
{
	private sealed record Profile(
		UnitStreamAbiProfileKind Kind,
		IReadOnlyDictionary<uint, UnitStreamAbiFormat> Formats);

	private sealed record Candidate(Profile Profile, string Signature, IReadOnlyList<string> Evidence);

	private static readonly Profile Legacy = new(
		UnitStreamAbiProfileKind.LegacyPre10800438,
		new Dictionary<uint, UnitStreamAbiFormat>
		{
			[0] = new("float", 4),
			[1] = new("vec2_float", 8),
			[2] = new("vec3_float", 12),
			[3] = new("vec4_float", 16),
			[4] = new("rgba_r8g8b8a8", 4),
			[20] = new("vec4_uint32", 16),
			[24] = new("vec4_uint8", 4),
			[25] = new("vec4_1010102", 4),
			[26] = new("unk_normal", 4),
			[29] = new("vec2_half", 4),
			[31] = new("vec4_half", 8)
		});

	private static readonly Profile Current = new(
		UnitStreamAbiProfileKind.Current10800438,
		new Dictionary<uint, UnitStreamAbiFormat>
		{
			[0] = new("float", 4),
			[1] = new("vec2_float", 8),
			[2] = new("vec3_float", 12),
			[3] = new("vec4_float", 16),
			[4] = new("rgba_r8g8b8a8", 4),
			[24] = new("vec4_uint32", 16),
			[28] = new("vec4_uint8", 4),
			[29] = new("vec4_1010102", 4),
			[30] = new("unk_normal", 4),
			[33] = new("vec2_half", 4),
			[35] = new("vec4_half", 8)
		});

	private static readonly Profile[] Profiles = [Legacy, Current];

	public static UnitStreamAbiResolution Resolve(uint unitVersion, IReadOnlyList<UnitStreamAbiRawStream> streams)
	{
		ArgumentNullException.ThrowIfNull(streams);
		if (streams.Count == 0)
		{
			var profile = GetExactProfile(unitVersion)?.Kind ?? UnitStreamAbiProfileKind.Current10800438;
			return new UnitStreamAbiResolution(
				UnitStreamAbiResolutionPath.ExactVersionProfile,
				profile,
				unitVersion,
				["Unit 不包含 StreamInfo。"]);
		}
		var evidence = new List<string>();
		var exact = GetExactProfile(unitVersion);
		if (exact is not null)
		{
			if (TryEvaluate(exact, streams, enforceSemanticCompatibility: false, out var candidate, out var rejection))
			{
				evidence.Add($"版本 {unitVersion} 命中 {exact.Kind}。");
				evidence.AddRange(candidate!.Evidence);
				return new UnitStreamAbiResolution(
					UnitStreamAbiResolutionPath.ExactVersionProfile,
					exact.Kind,
					unitVersion,
					evidence);
			}
			evidence.Add($"版本 {unitVersion} 的 {exact.Kind} 规则未通过结构校验：{rejection}");
		}

		var candidates = new List<Candidate>();
		var rejected = new List<string>();
		foreach (var profile in Profiles)
		{
			if (TryEvaluate(profile, streams, enforceSemanticCompatibility: true, out var candidate, out var rejection))
				candidates.Add(candidate!);
			else
				rejected.Add($"{profile.Kind}={rejection}");
		}

		if (candidates.Count == 1)
		{
			var candidate = candidates[0];
			evidence.Add($"未知版本按结构指纹唯一命中 {candidate.Profile.Kind}。");
			evidence.AddRange(candidate.Evidence);
			return new UnitStreamAbiResolution(
				UnitStreamAbiResolutionPath.FingerprintFallback,
				candidate.Profile.Kind,
				unitVersion,
				evidence);
		}

		if (candidates.Count > 1 && candidates.Select(candidate => candidate.Signature).Distinct(StringComparer.Ordinal).Count() == 1)
		{
			var candidate = candidates.FirstOrDefault(item => item.Profile.Kind == UnitStreamAbiProfileKind.Current10800438) ?? candidates[0];
			evidence.Add($"未知版本的多个 ABI profile 在该 Unit 上解码结果等价，按 {candidate.Profile.Kind} 继续。");
			evidence.AddRange(candidate.Evidence);
			return new UnitStreamAbiResolution(
				UnitStreamAbiResolutionPath.FingerprintFallback,
				candidate.Profile.Kind,
				unitVersion,
				evidence);
		}

		evidence.Add(candidates.Count == 0
			? $"没有已知 ABI profile 通过结构校验：{string.Join("; ", rejected)}"
			: $"结构指纹存在歧义：{string.Join(", ", candidates.Select(candidate => candidate.Profile.Kind))}");
		return new UnitStreamAbiResolution(
			UnitStreamAbiResolutionPath.Unresolved,
			UnitStreamAbiProfileKind.Unknown,
			unitVersion,
			evidence);
	}

	public static UnitStreamAbiFormat GetFormat(UnitStreamAbiResolution? resolution, uint format)
	{
		if (resolution is not null && resolution.IsResolved && GetProfile(resolution.Profile) is { } profile
			&& profile.Formats.TryGetValue(format, out var resolved))
			return resolved;
		return new UnitStreamAbiFormat("unknown", 0);
	}

	public static UnitStreamAbiProfileKind GetCanonicalWriterProfile(UnitMeshModel model)
	{
		ArgumentNullException.ThrowIfNull(model);
		if (model.StreamAbiResolution is { IsResolved: true } resolved
			&& resolved.UnitVersion == model.Version)
			return resolved.Profile;
		return GetExactProfile(model.Version)?.Kind ?? UnitStreamAbiProfileKind.Current10800438;
	}

	private static Profile? GetExactProfile(uint unitVersion)
		=> unitVersion is >= 10800433 and <= 10800437
			? Legacy
			: unitVersion == 10800438
				? Current
				: null;

	private static Profile? GetProfile(UnitStreamAbiProfileKind kind)
		=> kind switch
		{
			UnitStreamAbiProfileKind.LegacyPre10800438 => Legacy,
			UnitStreamAbiProfileKind.Current10800438 => Current,
			_ => null
		};

	private static bool TryEvaluate(
		Profile profile,
		IReadOnlyList<UnitStreamAbiRawStream> streams,
		bool enforceSemanticCompatibility,
		out Candidate? candidate,
		out string rejection)
	{
		var evidence = new List<string>();
		var signature = new List<string>();
		foreach (var stream in streams)
		{
			if (stream.Components.Count == 0)
			{
				if (stream.VertexStride != 0)
				{
					candidate = null;
					rejection = $"Stream {stream.Index} 没有组件但 stride={stream.VertexStride}";
					return false;
				}
				continue;
			}

			uint size = 0;
			var components = new List<string>(stream.Components.Count);
			foreach (var component in stream.Components)
			{
				if (component.Type > 7)
				{
					candidate = null;
					rejection = $"Stream {stream.Index} 包含未知语义 Type={component.Type}";
					return false;
				}
				if (!profile.Formats.TryGetValue(component.Format, out var format))
				{
					candidate = null;
					rejection = $"Stream {stream.Index} 的 Format={component.Format} 不在 {profile.Kind}";
					return false;
				}
				if (enforceSemanticCompatibility && !IsSemanticCompatible(component.Type, format))
				{
					candidate = null;
					rejection = $"Stream {stream.Index} 的 Type={component.Type}/Format={component.Format} ({format.Name}) 语义不兼容";
					return false;
				}
				size = checked(size + format.Size);
				components.Add($"{component.Type}:{component.Index}={component.Format}/{format.Name}/{format.Size}");
			}
			if (size != stream.VertexStride)
			{
				candidate = null;
				rejection = $"Stream {stream.Index} 的组件总宽度 {size} 与 stride {stream.VertexStride} 不一致";
				return false;
			}
			evidence.Add($"Stream {stream.Index} stride={stream.VertexStride}，组件=[{string.Join(",", components)}]");
			signature.Add($"{stream.Index}:{string.Join(",", components)}");
		}

		candidate = new Candidate(profile, string.Join("|", signature), evidence);
		rejection = string.Empty;
		return true;
	}

	private static bool IsSemanticCompatible(uint type, UnitStreamAbiFormat format)
		=> type switch
		{
			0 => format.Name is "vec3_float" or "vec4_float",
			1 or 2 or 3 => format.Name is "unk_normal" or "vec4_1010102" or "vec3_float" or "vec4_float",
			4 => format.Name is "vec2_float" or "vec2_half",
			5 => format.Name is "rgba_r8g8b8a8" or "vec4_1010102" or "vec4_float",
			6 => format.Name is "vec4_uint8" or "vec4_uint32",
			7 => format.Name is "vec4_half" or "vec4_float",
			_ => false
		};
}
