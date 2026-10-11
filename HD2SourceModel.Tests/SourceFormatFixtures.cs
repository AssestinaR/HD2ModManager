using System.Buffers.Binary;
using System.Text;

namespace HD2SourceModel.Tests;

/// <summary>
/// 按格式规范手写最小的 MDL / VVD / VTX / VPK 字节流。
/// <para>
/// 这样测试就不依赖机器上有没有装求生之路 2：合成样本覆盖"解析逻辑对不对"，
/// 真实 VPK 回归（<see cref="RealSourceModelTests"/>）只覆盖"格式理解对不对"。
/// 这些 builder 同时是可执行的格式文档——每个偏移都写明了它是什么。
/// </para>
/// </summary>
internal static class SourceFormatFixtures
{
	public const int MdlHeaderSize = 408;
	public const int MdlBoneStride = 216;
	public const int MdlTextureStride = 64;
	public const int MdlBodyPartStride = 16;
	public const int MdlModelStride = 148;
	public const int MdlMeshStride = 116;

	public const int MdlBonePoseToBoneOffset = 96;
	public const int MdlBoneProcTypeOffset = 164;
	public const int MdlBoneProcIndexOffset = 168;
	public const int JiggleBlockLength = 124;

	/// <summary>jigglebone 的参数个数（<c>mstudiojigglebone_t</c> 里 29 个 float，不含开头的 flags）。</summary>
	public const int JiggleValueCount = 29;

	/// <summary>一根骨骼。<paramref name="JiggleFlags"/> 为负表示这根骨不是 jiggle 骨。</summary>
	public sealed record BoneSpec(
		string? Name,
		int Parent,
		float[]? PoseToBone = null,
		int JiggleFlags = -1,
		float[]? JiggleValues = null);

	public sealed record MeshSpec(int Material, int VertexCount, int VertexStart);

	public sealed record ModelSpec(string Name, int VertexCount, int VertexIndex, MeshSpec[] Meshes);

	public sealed record BodyPartSpec(string Name, ModelSpec[] Models);

	public static float[] Identity3x4() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

	public static float[] Translation3x4(float x, float y, float z) => [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z];

	/// <summary>jiggle 参数的取值约定：第 i 个参数就是 <c>i + 0.5f</c>，便于逐项断言。</summary>
	public static float[] JiggleValues() =>
		[.. Enumerable.Range(0, JiggleValueCount).Select(index => index + 0.5f)];

	// ------------------------------------------------------------------ MDL
	public static byte[] BuildMdl(
		string name,
		int version,
		BoneSpec[] bones,
		BodyPartSpec[] parts,
		string[]? materials = null)
	{
		var materialCount = materials?.Length ?? 0;
		var jiggleCount = bones.Count(bone => bone.JiggleFlags >= 0);
		var modelCount = parts.Sum(part => part.Models.Length);
		var meshCount = parts.Sum(part => part.Models.Sum(model => model.Meshes.Length));

		var boneStart = MdlHeaderSize;
		var jiggleStart = boneStart + (bones.Length * MdlBoneStride);
		var textureStart = jiggleStart + (jiggleCount * JiggleBlockLength);
		var bodyPartStart = textureStart + (materialCount * MdlTextureStride);
		var modelStart = bodyPartStart + (parts.Length * MdlBodyPartStride);
		var meshStart = modelStart + (modelCount * MdlModelStride);
		var nameStart = meshStart + (meshCount * MdlMeshStride);

		// 名字池：bone / bodypart / material 的名字都用"相对自身结构体"的下标寻址，所以先占位再回填。
		var pool = new List<byte>();
		int PoolAdd(string value)
		{
			var at = nameStart + pool.Count;
			pool.AddRange(Encoding.ASCII.GetBytes(value));
			pool.Add(0);
			return at;
		}

		var boneNameAt = new int[bones.Length];
		for (var index = 0; index < bones.Length; index++)
			boneNameAt[index] = bones[index].Name is null ? 0 : PoolAdd(bones[index].Name!);

		var materialAt = new int[materialCount];
		for (var index = 0; index < materialCount; index++)
			materialAt[index] = PoolAdd(materials![index]);

		var bodyPartNameAt = new int[parts.Length];
		for (var index = 0; index < parts.Length; index++)
			bodyPartNameAt[index] = PoolAdd(parts[index].Name);

		var buffer = new byte[nameStart + pool.Count];

		// --- 文件头 ---
		Encoding.ASCII.GetBytes("IDST", buffer.AsSpan(0, 4));
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), version);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 0);
		WriteFixedString(buffer, 12, 64, name);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(156), bones.Length);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(160), boneStart);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(204), materialCount);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(208), textureStart);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(232), parts.Length);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(236), bodyPartStart);

		// --- 骨骼 ---
		var jiggleCursor = 0;
		for (var index = 0; index < bones.Length; index++)
		{
			var entry = boneStart + (index * MdlBoneStride);
			var bone = bones[index];
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry), bone.Name is null ? 0 : boneNameAt[index] - entry);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 4), bone.Parent);

			var pose = bone.PoseToBone ?? Identity3x4();
			for (var k = 0; k < 12; k++)
				BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(entry + MdlBonePoseToBoneOffset + (k * 4)), pose[k]);

			if (bone.JiggleFlags < 0)
				continue;

			var values = bone.JiggleValues ?? JiggleValues();
			var block = jiggleStart + (jiggleCursor++ * JiggleBlockLength);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + MdlBoneProcTypeOffset), 5);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + MdlBoneProcIndexOffset), block - entry);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(block), bone.JiggleFlags);
			for (var k = 0; k < JiggleValueCount; k++)
				BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(block + 4 + (k * 4)), values[k]);
		}

		// --- 材质路径 ---
		for (var index = 0; index < materialCount; index++)
		{
			var entry = textureStart + (index * MdlTextureStride);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry), materialAt[index] - entry);
		}

		// --- bodypart / model / mesh ---
		var globalModel = 0;
		var globalMesh = 0;
		for (var partIndex = 0; partIndex < parts.Length; partIndex++)
		{
			var entry = bodyPartStart + (partIndex * MdlBodyPartStride);
			var part = parts[partIndex];
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry), bodyPartNameAt[partIndex] - entry);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 4), part.Models.Length);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 8), 0);
			var firstModelAt = modelStart + (globalModel * MdlModelStride);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 12), firstModelAt - entry);

			foreach (var model in part.Models)
			{
				var modelEntry = modelStart + (globalModel * MdlModelStride);
				WriteFixedString(buffer, modelEntry, 64, model.Name);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry + 72), model.Meshes.Length);
				var firstMeshAt = meshStart + (globalMesh * MdlMeshStride);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry + 76), firstMeshAt - modelEntry);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry + 80), model.VertexCount);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry + 84), model.VertexIndex);

				foreach (var mesh in model.Meshes)
				{
					var meshEntry = meshStart + (globalMesh * MdlMeshStride);
					BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(meshEntry), mesh.Material);
					BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(meshEntry + 8), mesh.VertexCount);
					BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(meshEntry + 12), mesh.VertexStart);
					globalMesh++;
				}

				globalModel++;
			}
		}

		pool.CopyTo(buffer, nameStart);
		return buffer;
	}

	// ------------------------------------------------------------------ VVD
	public sealed record VertexSpec(float[] Weights, byte[] Bones, Vec3 Position, Vec3 Normal, float U, float V);

	public sealed record FixupSpec(int Lod, int VertexIndex, int Count);

	public static byte[] BuildVvd(VertexSpec[] stored, FixupSpec[]? fixups = null)
	{
		const int headerSize = 64;
		const int vertexStride = 48;
		const int fixupStride = 12;

		var fixupCount = fixups?.Length ?? 0;
		var vertexOffset = headerSize;
		var fixupOffset = vertexOffset + (stored.Length * vertexStride);
		var buffer = new byte[fixupOffset + (fixupCount * fixupStride)];

		Encoding.ASCII.GetBytes("IDSV", buffer.AsSpan(0, 4));
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 4);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 0);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), 1);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), stored.Length);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(48), fixupCount);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(52), fixupCount > 0 ? fixupOffset : 0);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(56), vertexOffset);

		for (var index = 0; index < stored.Length; index++)
		{
			var at = vertexOffset + (index * vertexStride);
			for (var k = 0; k < 3; k++)
				BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + (k * 4)), stored[index].Weights[k]);

			buffer[at + 12] = stored[index].Bones[0];
			buffer[at + 13] = stored[index].Bones[1];
			buffer[at + 14] = stored[index].Bones[2];
			buffer[at + 15] = (byte)stored[index].Weights.Count(weight => weight > 0);

			WriteVec3(buffer, at + 16, stored[index].Position);
			WriteVec3(buffer, at + 28, stored[index].Normal);
			BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 40), stored[index].U);
			BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 44), stored[index].V);
		}

		for (var index = 0; index < fixupCount; index++)
		{
			var at = fixupOffset + (index * fixupStride);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at), fixups![index].Lod);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 4), fixups[index].VertexIndex);
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at + 8), fixups[index].Count);
		}

		return buffer;
	}

	// ------------------------------------------------------------------ VTX
	public sealed record VtxGroupSpec(ushort[] OriginalVertices, ushort[] Indices);

	/// <summary>一个 model：<c>Meshes[meshIndex]</c> 是该 mesh 的 strip group 列表。</summary>
	public sealed record VtxModelSpec(VtxGroupSpec[][] Meshes);

	public sealed record VtxBodyPartSpec(VtxModelSpec[] Models);

	public static VtxGroupSpec Group(ushort[] originalVertices, ushort[] indices) => new(originalVertices, indices);

	/// <summary>只有一个 mesh 的 model。</summary>
	public static VtxModelSpec SingleMesh(params VtxGroupSpec[] groups) => new([groups]);

	public static VtxModelSpec Model(params VtxGroupSpec[][] meshes) => new(meshes);

	public static VtxBodyPartSpec BodyPart(params VtxModelSpec[] models) => new(models);

	/// <summary>
	/// 造一个 <c>bodypart → model → LOD → mesh → strip group</c> 的 VTX。
	/// <para>
	/// <b>所有 strip group 的结构体是连续排列的</b>（与其余几层一样），顶点与索引数据另在结构体数组之后，
	/// 这正是"步长选错就必然越界"的原因。真实文件就是这个形状。
	/// </para>
	/// <para>
	/// <paramref name="stripGroupStride"/> 显式给出，以便单独测"步长与版本号不符"的情况。
	/// </para>
	/// </summary>
	public static byte[] BuildVtx(int version, int stripGroupStride, VtxBodyPartSpec[] bodyParts)
	{
		const int headerSize = 36;
		const int bodyPartStride = 8;
		const int modelStride = 8;
		const int lodStride = 12;
		const int meshStride = 9;
		const int vertexStride = 9;

		var totalModels = bodyParts.Sum(bodyPart => bodyPart.Models.Length);
		var totalMeshes = bodyParts.Sum(bodyPart => bodyPart.Models.Sum(model => model.Meshes.Length));
		var groups = bodyParts
			.SelectMany(bodyPart => bodyPart.Models)
			.SelectMany(model => model.Meshes)
			.SelectMany(mesh => mesh)
			.ToArray();

		var bodyPartStart = headerSize;
		var modelStart = bodyPartStart + (bodyParts.Length * bodyPartStride);
		var lodStart = modelStart + (totalModels * modelStride);
		var meshStart = lodStart + (totalModels * lodStride);
		var groupStart = meshStart + (totalMeshes * meshStride);
		var dataStart = groupStart + (groups.Length * stripGroupStride);

		var vertexStarts = new int[groups.Length];
		var indexStarts = new int[groups.Length];
		var cursor = dataStart;
		for (var index = 0; index < groups.Length; index++)
		{
			vertexStarts[index] = cursor;
			cursor += groups[index].OriginalVertices.Length * vertexStride;
			indexStarts[index] = cursor;
			cursor += groups[index].Indices.Length * 2;
		}

		var buffer = new byte[cursor];

		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), version);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), 3);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), 1);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), bodyParts.Length);
		BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(32), bodyPartStart);

		var modelCursor = 0;
		var meshCursor = 0;
		var groupCursor = 0;
		for (var bodyPartIndex = 0; bodyPartIndex < bodyParts.Length; bodyPartIndex++)
		{
			var bodyPartEntry = bodyPartStart + (bodyPartIndex * bodyPartStride);
			var models = bodyParts[bodyPartIndex].Models;
			BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(bodyPartEntry), models.Length);
			BinaryPrimitives.WriteInt32LittleEndian(
				buffer.AsSpan(bodyPartEntry + 4),
				models.Length > 0 ? modelStart + (modelCursor * modelStride) - bodyPartEntry : 0);

			foreach (var model in models)
			{
				var modelEntry = modelStart + (modelCursor * modelStride);
				var lodEntry = lodStart + (modelCursor * lodStride);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry), 1);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(modelEntry + 4), lodEntry - modelEntry);
				BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(lodEntry), model.Meshes.Length);
				BinaryPrimitives.WriteInt32LittleEndian(
					buffer.AsSpan(lodEntry + 4),
					model.Meshes.Length > 0 ? meshStart + (meshCursor * meshStride) - lodEntry : 0);

				foreach (var mesh in model.Meshes)
				{
					var meshEntry = meshStart + (meshCursor * meshStride);
					BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(meshEntry), mesh.Length);
					BinaryPrimitives.WriteInt32LittleEndian(
						buffer.AsSpan(meshEntry + 4),
						mesh.Length > 0 ? groupStart + (groupCursor * stripGroupStride) - meshEntry : 0);

					foreach (var group in mesh)
					{
						var entry = groupStart + (groupCursor * stripGroupStride);
						var vertexCount = group.OriginalVertices.Length;
						var indexCount = group.Indices.Length;

						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry), vertexCount);
						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 4), vertexStarts[groupCursor] - entry);
						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 8), indexCount);
						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 12), indexStarts[groupCursor] - entry);
						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 16), 0);
						BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(entry + 20), 0);

						for (var vertex = 0; vertex < vertexCount; vertex++)
						{
							var at = vertexStarts[groupCursor] + (vertex * vertexStride);
							buffer[at + 3] = 1;
							BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at + 4), group.OriginalVertices[vertex]);
						}

						for (var slot = 0; slot < indexCount; slot++)
							BinaryPrimitives.WriteUInt16LittleEndian(
								buffer.AsSpan(indexStarts[groupCursor] + (slot * 2)), group.Indices[slot]);

						groupCursor++;
					}

					meshCursor++;
				}

				modelCursor++;
			}
		}

		return buffer;
	}

	// ------------------------------------------------------------------ VPK
	public sealed record VpkFileSpec(string Extension, string Directory, string FileName, byte[] Preload, byte[] Payload);

	/// <summary>
	/// <paramref name="entrySize"/> 单独给出，因为实测文件里"版本号"与"条目步长"并不一致：
	/// 全部 97 个求生之路 2 的 VPK 都是版本 1 + 18 字节条目。
	/// </summary>
	public static byte[] BuildVpk(int version, int entrySize, VpkFileSpec[] files)
	{
		var headerSize = version == 1 ? 12 : 28;

		// 数据段的偏移先算好：条目头里写的就是"相对数据段起点"的偏移。
		var payloads = new List<byte>();
		var offsets = new int[files.Length];
		for (var index = 0; index < files.Length; index++)
		{
			offsets[index] = payloads.Count;
			payloads.AddRange(files[index].Payload);
		}

		var tree = new List<byte>();
		foreach (var extensionGroup in files.Select((file, index) => (file, index)).GroupBy(pair => pair.file.Extension))
		{
			WriteString(tree, extensionGroup.Key);
			foreach (var directoryGroup in extensionGroup.GroupBy(pair => pair.file.Directory))
			{
				WriteString(tree, directoryGroup.Key);
				foreach (var (file, index) in directoryGroup)
				{
					WriteString(tree, file.FileName);

					var header = new byte[entrySize];
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0);
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)file.Preload.Length);
					BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x7FFF);
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)offsets[index]);
					BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)file.Payload.Length);
					if (entrySize >= 18)
						BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 0xFFFF);

					tree.AddRange(header);
					tree.AddRange(file.Preload);
				}

				WriteString(tree, string.Empty);
			}

			WriteString(tree, string.Empty);
		}

		WriteString(tree, string.Empty);

		var result = new byte[headerSize + tree.Count + payloads.Count];
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x55AA1234);
		BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), version);
		BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)tree.Count);
		tree.CopyTo(result, headerSize);
		payloads.CopyTo(result, headerSize + tree.Count);
		return result;
	}

	/// <summary>把字节流写进一个临时文件，返回路径；调用方负责删除。</summary>
	public static string WriteTempFile(string extension, byte[] content)
	{
		var path = Path.Combine(Path.GetTempPath(), $"hd2src-{Guid.NewGuid():N}{extension}");
		File.WriteAllBytes(path, content);
		return path;
	}

	private static void WriteString(List<byte> buffer, string value)
	{
		buffer.AddRange(Encoding.UTF8.GetBytes(value));
		buffer.Add(0);
	}

	private static void WriteFixedString(byte[] buffer, int offset, int length, string value)
	{
		var bytes = Encoding.ASCII.GetBytes(value);
		if (bytes.Length >= length)
			throw new ArgumentException($"字符串 \"{value}\" 放不进 {length} 字节的定长字段。", nameof(value));

		Array.Copy(bytes, 0, buffer, offset, bytes.Length);
	}

	private static void WriteVec3(byte[] buffer, int offset, Vec3 value)
	{
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), (float)value.X);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 4), (float)value.Y);
		BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset + 8), (float)value.Z);
	}
}
