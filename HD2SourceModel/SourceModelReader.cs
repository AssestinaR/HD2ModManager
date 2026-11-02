using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HD2SourceModel;

/// <summary>
/// 解析 Source 引擎的 <c>.mdl</c>：骨架（含 jigglebone 参数）、bodypart / model / mesh 结构、材质路径。
/// <para>
/// 本类只读文件、只产出记录，不抛异常处理"数据不对"——凡是可疑之处都记进
/// <see cref="SourceModelDocument.Diagnostics"/>，让 <c>legacy inspect</c> 能把问题报出来而不是崩掉。
/// </para>
/// <para>
/// Parses a Source <c>.mdl</c>: the skeleton (including jigglebone parameters), the
/// bodypart/model/mesh tree, and the material paths.  Questionable data becomes a diagnostic
/// rather than an exception, so an inspection command can report it instead of crashing.
/// </para>
/// </summary>
public static class SourceModelReader
{
	/// <summary>
	/// <c>mstudiobone_t</c> 在 v44..v53 都是 216 字节。这个范围是实测出来的，不是猜的：
	/// 本机 97 个求生之路 2 的 VPK 里主模型版本分别是 44 / 48 / 49 / 53，用 216 解析<b>全部</b>
	/// 通过自洽性检查（父级下标全部落在范围内、bind 矩阵全部可逆），用 184 则几乎全部失败。
	/// </summary>
	private const int MinimumBoneStrideVersion = 44;

	/// <summary>见 <see cref="MinimumBoneStrideVersion"/>；v53 是求生之路 2 实际使用的版本。</summary>
	private const int MaximumBoneStrideVersion = 53;

	private const int BoneStride = 216;
	private const int BonePoseToBoneOffset = 96;
	private const int BoneProcTypeOffset = 164;
	private const int BoneProcIndexOffset = 168;

	private const int TextureStride = 64;
	private const int BodyPartStride = 16;
	private const int ModelStride = 148;
	private const int MeshStride = 116;

	private const int VersionOffset = 4;
	private const int ChecksumOffset = 8;
	private const int NameOffset = 12;
	private const int NameLength = 64;
	private const int BoneCountOffset = 156;
	private const int BoneOffsetOffset = 160;
	private const int TextureCountOffset = 204;
	private const int TextureOffsetOffset = 208;
	// 材质**目录**表：引擎按这张表的顺序找 materials/<目录>/<名字>.vmt，第一个存在的胜出。
	private const int CdTextureCountOffset = 212;
	private const int CdTextureOffsetOffset = 216;
	private const int BodyPartCountOffset = 232;
	private const int BodyPartOffsetOffset = 236;

	/// <summary><c>STUDIO_PROC_JIGGLE</c>：<c>proctype</c> 取这个值表示该骨挂了弹簧参数。</summary>
	private const int ProcedureTypeJiggle = 5;

	private const int JiggleParameterCount = 29;
	private const int JiggleBlockLength = 4 + (JiggleParameterCount * 4);

	private const int MaxBoneCount = 4096;
	private const int MaxBodyPartCount = 1024;
	private const int MaxModelCountPerBodyPart = 1024;
	private const int MaxMeshCountPerModel = 4096;
	private const int MaxTextureCount = 4096;
	private const int MaxCdTextureCount = 256;
	private const int VertexStride = 48;

	public static SourceModelDocument Read(byte[] mdl)
	{
		ArgumentNullException.ThrowIfNull(mdl);
		var diagnostics = new List<SourceModelDiagnostic>();

		if (mdl.Length < 408)
			throw new InvalidDataException($"MDL 太小（{mdl.Length} 字节），不是有效的模型文件。");

		if (mdl[0] != (byte)'I' || mdl[1] != (byte)'D' || mdl[2] != (byte)'S' || mdl[3] != (byte)'T')
			throw new InvalidDataException("MDL magic 不是 IDST，不是 Source 模型文件。");

		var version = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(VersionOffset));
		if (version is < MinimumBoneStrideVersion or > MaximumBoneStrideVersion)
		{
			// 结构体步长按版本变化，本实现只覆盖 L4D2 所在的那一段；其余版本直接拒绝而不是猜。
			throw new NotSupportedException(
				$"MDL 版本 {version} 不在支持范围 v{MinimumBoneStrideVersion}..v{MaximumBoneStrideVersion}，" +
				"骨骼结构体步长可能与本实现不同。");
		}

		var checksum = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(ChecksumOffset));
		var name = ReadCString(mdl, NameOffset, NameLength) ?? string.Empty;

		var bones = ReadBones(mdl, diagnostics);
		// 给占位骨名加**按来源模型派生**的命名空间（见 SourceBone.DisplayName 的 remarks）。
		// 在这里统一施加：读骨的时候还不知道模型身份，读完就能算了。
		bones = ApplyBoneNamespace(bones, name, checksum);
		var materials = ReadMaterialPaths(mdl, diagnostics);
		var materialRefs = ReadMaterialRefs(mdl, materials, diagnostics);
		var bodyParts = ReadBodyParts(mdl, diagnostics);

		return new SourceModelDocument(version, name, checksum, bones, bodyParts, materials, diagnostics)
		{
			MaterialRefs = materialRefs,
			MaterialFolders = ReadCdTextures(mdl, diagnostics),
		};
	}

	/// <summary>
	/// 读 mdl 头部声明的**材质目录表**（<c>cdtextures</c>）。
	/// <para>
	/// <b>这张表是"模型需要哪些材质"的权威答案。</b>引擎解析材质名时按这张表的**顺序**找
	/// <c>materials/&lt;目录&gt;/&lt;名字&gt;.vmt</c>，第一个存在的胜出；一个都找不到就是紫块。
	/// 以前我们只读了材质**名**、丢掉了目录，于是"同名的两个材质分属不同目录"时必然串味，
	/// 也没法回答"这个模型到底缺哪个材质"。
	/// </para>
	/// <para>目录串以反斜杠结尾（如 <c>diyu2026\GF2Exilium\Cheyanne\MaidenDebut\P2\</c>），这里统一成正斜杠、去尾斜杠。</para>
	/// </summary>
	private static IReadOnlyList<string> ReadCdTextures(byte[] mdl, List<SourceModelDiagnostic> diagnostics)
	{
		var count = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(CdTextureCountOffset));
		var offset = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(CdTextureOffsetOffset));
		if (count <= 0)
			return [];
		if (count > MaxCdTextureCount || offset <= 0 || offset + (count * 4) > mdl.Length)
		{
			diagnostics.Add(new SourceModelDiagnostic(
				"CdTextureTableUnreadable", $"材质目录表读不出来：count={count} offset={offset}。"));
			return [];
		}

		var folders = new List<string>(count);
		for (var index = 0; index < count; index++)
		{
			var stringOffset = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(offset + (index * 4)));
			var raw = stringOffset > 0 ? ReadCString(mdl, stringOffset, 260) : null;
			var normalized = (raw ?? string.Empty).Replace('\\', '/').Trim('/');
			if (normalized.Length != 0) folders.Add(normalized);
		}

		return folders;
	}

	/// <summary>
	/// 把"材质名"展开成"候选全路径"：<b>每个名字 × 每个材质目录</b>。
	/// <para>
	/// 为什么不挑一个目录：引擎是"按顺序试着找"，所以同一个名字在 P2 / P3 / base 三个目录里
	/// 都可能命中，而内容是不同的材质。把候选路径全给出来，调用方才能做精确的"谁提供了它"判断，
	/// 也能按顺序决定优先生效的那一份。
	/// </para>
	/// <para>没有材质目录表的模型（老包）退化成"只有名字、目录为空"。</para>
	/// </summary>
	private static IReadOnlyList<SourceMaterialRef> ReadMaterialRefs(
		byte[] mdl,
		IReadOnlyList<string> names,
		List<SourceModelDiagnostic> diagnostics)
	{
		if (names.Count == 0) return [];
		var folders = ReadCdTextures(mdl, diagnostics);
		if (folders.Count == 0)
			return names.Select(name => new SourceMaterialRef(name, string.Empty, 0)).ToArray();

		var refs = new List<SourceMaterialRef>(names.Count * folders.Count);
		for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
		{
			foreach (var name in names)
				refs.Add(new SourceMaterialRef(name, folders[folderIndex], folderIndex));
		}

		return refs;
	}

	/// <summary>
	/// 派生并施加占位骨名的命名空间。
	/// <para>
	/// 种子取 <c>模型名 + checksum + 每根骨的(名字, 父下标)</c>：同一份模型重复读得到同一个 token
	/// （重转同一份包不会变出新名字），不同模型几乎不可能撞。
	/// 用 6 字节摘要（12 个十六进制位），1000 个模型时碰撞概率约 1e-9。
	/// </para>
	/// </summary>
	private static IReadOnlyList<SourceBone> ApplyBoneNamespace(
		IReadOnlyList<SourceBone> bones, string name, int checksum)
	{
		var seed = new StringBuilder();
		seed.Append(name).Append('|').Append(checksum);
		foreach (var bone in bones)
			seed.Append('|').Append(bone.Index).Append(':').Append(bone.Name ?? "").Append('@').Append(bone.ParentIndex);
		var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
		var token = "j" + Convert.ToHexString(digest, 0, 6).ToLowerInvariant();

		var result = new List<SourceBone>(bones.Count);
		foreach (var bone in bones)
			result.Add(bone with { Namespace = token });
		return result;
	}

	private static IReadOnlyList<SourceBone> ReadBones(byte[] mdl, List<SourceModelDiagnostic> diagnostics)
	{
		var count = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(BoneCountOffset));
		var offset = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(BoneOffsetOffset));
		if (count <= 0 || count > MaxBoneCount)
			throw new InvalidDataException($"MDL 骨骼数量异常：{count}。");
		if (offset <= 0 || offset + (count * BoneStride) > mdl.Length)
			throw new InvalidDataException($"MDL 骨骼数组越界：offset={offset} count={count}。");

		var bones = new List<SourceBone>(count);
		for (var index = 0; index < count; index++)
		{
			var entry = offset + (index * BoneStride);
			var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry));
			var parent = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 4));

			var raw = new float[12];
			for (var i = 0; i < 12; i++)
				raw[i] = BinaryPrimitives.ReadSingleLittleEndian(mdl.AsSpan(entry + BonePoseToBoneOffset + (i * 4)));

			var poseToBone = Mat4.FromSource3x4(raw);
			if (!poseToBone.TryInvert(out var worldBind))
			{
				worldBind = Mat4.Identity;
				diagnostics.Add(new SourceModelDiagnostic(
					"NonInvertibleBindMatrix",
					$"骨骼 {index} 的 poseToBone 不可逆，已按单位矩阵处理。"));
			}

			var name = ReadBoneName(mdl, entry, nameIndex, index, diagnostics);
			bones.Add(new SourceBone(index, name, parent, poseToBone, worldBind,
				ReadJiggleBone(mdl, entry, index, diagnostics)));
		}

		return bones;
	}

	/// <summary>
	/// 读骨骼名。名字读不出来是<b>常态</b>而不是异常：这些模型的 <c>sznameindex</c> 指向内嵌的
	/// SMD 文本，读到的是带换行和引号的段落，于是被判定为"没有名字"。父级下标不受影响，
	/// 所以层级结构仍然完整可用。
	/// </summary>
	private static string? ReadBoneName(
		byte[] mdl, int boneEntry, int nameIndex, int boneIndex, List<SourceModelDiagnostic> diagnostics)
	{
		if (nameIndex <= 0)
			return null;

		var raw = ReadCString(mdl, boneEntry + nameIndex, 128);
		if (raw is null)
		{
			diagnostics.Add(new SourceModelDiagnostic(
				"BoneNameOutOfRange", $"骨骼 {boneIndex} 的名字下标越界。"));
			return null;
		}

		if (raw.Length == 0 || !IsPlausibleBoneName(raw))
			return null;

		return raw;
	}

	private static bool IsPlausibleBoneName(string value)
	{
		foreach (var character in value)
		{
			var acceptable = character is '_' or '.' or '-'
				|| (character >= '0' && character <= '9')
				|| (character >= 'a' && character <= 'z')
				|| (character >= 'A' && character <= 'Z');
			if (!acceptable)
				return false;
		}

		return true;
	}

	private static SourceJiggleBone? ReadJiggleBone(
		byte[] mdl, int boneEntry, int boneIndex, List<SourceModelDiagnostic> diagnostics)
	{
		var procedureType = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(boneEntry + BoneProcTypeOffset));
		if (procedureType != ProcedureTypeJiggle)
			return null;

		// procindex 是相对"该骨骼结构体自身地址"的字节偏移，不是相对文件头。
		var procedureIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(boneEntry + BoneProcIndexOffset));
		var block = boneEntry + procedureIndex;
		if (block < 0 || block + JiggleBlockLength > mdl.Length)
		{
			diagnostics.Add(new SourceModelDiagnostic(
				"JiggleParametersOutOfRange",
				$"骨骼 {boneIndex} 标记为 jiggle 但参数块越界（procindex={procedureIndex}）。"));
			return null;
		}

		var flags = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(block));
		Span<float> values = stackalloc float[JiggleParameterCount];
		for (var i = 0; i < JiggleParameterCount; i++)
			values[i] = BinaryPrimitives.ReadSingleLittleEndian(mdl.AsSpan(block + 4 + (i * 4)));

		return new SourceJiggleBone(
			flags,
			values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
			values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15],
			values[16], values[17], values[18], values[19], values[20], values[21], values[22], values[23],
			values[24], values[25], values[26], values[27], values[28]);
	}

	private static IReadOnlyList<string> ReadMaterialPaths(byte[] mdl, List<SourceModelDiagnostic> diagnostics)
	{
		var count = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(TextureCountOffset));
		var offset = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(TextureOffsetOffset));
		if (count <= 0 || count > MaxTextureCount || offset <= 0 || offset + (count * TextureStride) > mdl.Length)
		{
			if (count != 0)
			{
				diagnostics.Add(new SourceModelDiagnostic(
					"MaterialTableUnreadable", $"材质表读不出来：count={count} offset={offset}。"));
			}

			return [];
		}

		var paths = new List<string>(count);
		for (var index = 0; index < count; index++)
		{
			var entry = offset + (index * TextureStride);
			var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry));
			paths.Add(nameIndex > 0 ? ReadCString(mdl, entry + nameIndex, 260) ?? string.Empty : string.Empty);
		}

		return paths;
	}

	private static IReadOnlyList<SourceBodyPart> ReadBodyParts(byte[] mdl, List<SourceModelDiagnostic> diagnostics)
	{
		var bodyPartCount = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(BodyPartCountOffset));
		var bodyPartOffset = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(BodyPartOffsetOffset));
		if (bodyPartCount <= 0 || bodyPartCount > MaxBodyPartCount)
			throw new InvalidDataException($"MDL bodypart 数量异常：{bodyPartCount}。");
		if (bodyPartOffset <= 0 || bodyPartOffset + (bodyPartCount * BodyPartStride) > mdl.Length)
			throw new InvalidDataException($"MDL bodypart 数组越界：offset={bodyPartOffset} count={bodyPartCount}。");

		var bodyParts = new List<SourceBodyPart>(bodyPartCount);
		for (var bodyPartIndex = 0; bodyPartIndex < bodyPartCount; bodyPartIndex++)
		{
			var entry = bodyPartOffset + (bodyPartIndex * BodyPartStride);
			var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry));
			var modelCount = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 4));
			// modelindex 是相对本结构体自身地址的偏移。
			var modelIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 12));

			var bodyPartName = nameIndex > 0
				? ReadCString(mdl, entry + nameIndex, 64) ?? $"bodypart_{bodyPartIndex}"
				: $"bodypart_{bodyPartIndex}";

			if (modelCount < 0 || modelCount > MaxModelCountPerBodyPart)
			{
				diagnostics.Add(new SourceModelDiagnostic(
					"BodyPartModelCountInvalid", $"bodypart {bodyPartName} 的 model 数量异常：{modelCount}。"));
				bodyParts.Add(new SourceBodyPart(bodyPartName, []));
				continue;
			}

			var models = new List<SourceBodyPartModel>(modelCount);
			if (modelCount > 0)
			{
				var modelStart = entry + modelIndex;
				if (modelStart < 0 || modelStart + (modelCount * ModelStride) > mdl.Length)
				{
					diagnostics.Add(new SourceModelDiagnostic(
						"BodyPartModelsOutOfRange", $"bodypart {bodyPartName} 的 model 数组越界。"));
					bodyParts.Add(new SourceBodyPart(bodyPartName, []));
					continue;
				}

				for (var modelSlot = 0; modelSlot < modelCount; modelSlot++)
					models.Add(ReadBodyPartModel(mdl, modelStart + (modelSlot * ModelStride), bodyPartName, diagnostics));
			}

			bodyParts.Add(new SourceBodyPart(bodyPartName, models));
		}

		return bodyParts;
	}

	private static SourceBodyPartModel ReadBodyPartModel(
		byte[] mdl, int entry, string bodyPartName, List<SourceModelDiagnostic> diagnostics)
	{
		var name = ReadCString(mdl, entry, NameLength) ?? string.Empty;
		if (name.Length == 0)
			name = $"model@{entry}";

		var meshCount = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 72));
		var meshIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 76));
		var vertexCount = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 80));
		var vertexIndex = BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(entry + 84));

		if (vertexCount < 0 || meshCount < 0 || meshCount > MaxMeshCountPerModel)
		{
			diagnostics.Add(new SourceModelDiagnostic(
				"ModelCountsInvalid", $"{bodyPartName}/{name} 的 mesh 或顶点数量异常。"));
			return new SourceBodyPartModel(name, 0, 0, []);
		}

		if (vertexIndex < 0 || vertexIndex % VertexStride != 0)
		{
			// 不是 48 的倍数就无法换算成 VVD 的顶点下标，继续下去只会产出错位的几何。
			diagnostics.Add(new SourceModelDiagnostic(
				"ModelVertexIndexMisaligned",
				$"{bodyPartName}/{name} 的 vertexindex={vertexIndex} 不是 {VertexStride} 的倍数，已跳过该 model。"));
			return new SourceBodyPartModel(name, 0, 0, []);
		}

		var meshes = new List<SourceMesh>(meshCount);
		if (meshCount > 0)
		{
			var meshStart = entry + meshIndex;
			if (meshStart < 0 || meshStart + (meshCount * MeshStride) > mdl.Length)
			{
				diagnostics.Add(new SourceModelDiagnostic(
					"ModelMeshesOutOfRange", $"{bodyPartName}/{name} 的 mesh 数组越界。"));
				return new SourceBodyPartModel(name, 0, 0, []);
			}

			for (var meshSlot = 0; meshSlot < meshCount; meshSlot++)
			{
				var meshEntry = meshStart + (meshSlot * MeshStride);
				meshes.Add(new SourceMesh(
					BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(meshEntry)),
					BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(meshEntry + 8)),
					BinaryPrimitives.ReadInt32LittleEndian(mdl.AsSpan(meshEntry + 12))));
			}
		}

		return new SourceBodyPartModel(name, vertexCount, vertexIndex / VertexStride, meshes);
	}

	/// <summary>
	/// 读一个以 NUL 结尾的字符串；起始下标越界或找不到结尾都返回 <c>null</c>。
	/// 与"读到空串"区分开：空串是"这里确实没有名字"，<c>null</c> 是"下标本身有问题"。
	/// <para>
	/// 按 <b>UTF-8</b> 解码而不是 ASCII：模型的 bodypart / model 名常带作者的中文标注
	/// （实测例如 <c>裙Dress_Skirt</c>、<c>猫耳CatEars</c>、<c>厚底高跟platform shoes</c>），
	/// 用 ASCII 解会把它们变成一串 <c>?</c>。
	/// </para>
	/// </summary>
	private static string? ReadCString(byte[] buffer, int start, int limit)
	{
		if (start <= 0 || start >= buffer.Length)
			return null;

		var end = Array.IndexOf(buffer, (byte)0, start, Math.Min(limit, buffer.Length - start));
		if (end < 0)
			return null;

		return Encoding.UTF8.GetString(buffer, start, end - start);
	}
}
