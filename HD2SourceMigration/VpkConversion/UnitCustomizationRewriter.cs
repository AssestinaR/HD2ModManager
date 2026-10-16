using System.Buffers.Binary;
using System.Text;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 改写一个 Unit TocData 里的 <c>CustomizationInfo</c> 记录。
/// <para>
/// <b>为什么需要动字节手术。</b>这条记录是<b>变长</b>的，布局是
/// <c>24 字节头 + [u32 长度 + 串] + 12 + [u32 长度 + 串] + 12 + …</c>（四段：bodyType / slot /
/// weight / pieceType），而读取方在每个串之后<b>固定跳 12 字节</b>。所以下一个字段的位置由上一个串的
/// <b>长度</b>决定——把 "Hip" 换成 "Torso" 会让后面所有字段整体平移，整条记录的大小随之变化。
/// </para>
/// <para>
/// <c>CanonicalUnitRebuilder</c> 只把这条记录的<b>偏移</b>平移（<c>ShiftOffsets</c>），字节原样保留，
/// 所以它改不了槽位。这里的做法与它的 <c>RelocateTransformInfo</c> 同构：把新记录拼进去，
/// 再把<b>落在这条记录之后</b>的头部绝对偏移一起平移。
/// </para>
/// <para>
/// 为什么非改不可：Unit 自己的槽位决定了管理器怎么给它分类（<c>UnitMeshPartClassifier</c> 读的就是
/// 这里），而分类结果又决定 <c>custom-model.json</c> 里每个 piece 的 (slot, pieceType)。
/// 三十个配件如果全都继承同一个外壳的槽位，它们会在同一个 <c>body:slot:piece_type</c> 上撞车，
/// 结果就是只有一件能显示。
/// </para>
/// </summary>
public static class UnitCustomizationRewriter
{
	private const int HeaderSize = 24;
	private const int GapAfterString = 12;

	/// <summary>头部里所有<b>绝对偏移</b>字段的位置。顺序与 <c>CanonicalUnitRebuilder.ShiftOffsets</c> 一致。</summary>
	private static readonly int[] HeaderOffsetFields =
	[
		0x4c, // CustomizationInfo
		0x50, // UnkHeaderOffset1
		0x54, // UnkHeaderOffset2
		0x58, // BoneInfo
		0x5c, // StreamInfo
		0x60, // Ending
		0x64, // MeshInfo
		0x70, // Materials
	];

	public static (byte[] TocData, UnitMeshModel Model) Rewrite(
		UnitMeshModel model, byte[] tocData, UnitCustomizationInfo customization, List<string> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(tocData);
		ArgumentNullException.ThrowIfNull(customization);
		ArgumentNullException.ThrowIfNull(diagnostics);

		var offset = checked((int)model.CustomizationInfoOffset);
		if (offset <= 0 || offset + HeaderSize > tocData.Length)
		{
			diagnostics.Add($"CustomizationInfoOffset {offset} 越界（TocData {tocData.Length} 字节），无法改写槽位。");
			return (tocData, model);
		}

		var oldLength = Measure(tocData, offset, diagnostics);
		if (oldLength <= 0)
			return (tocData, model);

		var replacement = Serialize(customization);
		if (oldLength == replacement.Length)
		{
			replacement.CopyTo(tocData, offset);
			return (tocData, model);
		}

		var delta = replacement.Length - oldLength;
		var output = new byte[checked(tocData.Length + delta)];
		tocData.AsSpan(0, offset).CopyTo(output);
		replacement.CopyTo(output, offset);
		tocData.AsSpan(offset + oldLength).CopyTo(output.AsSpan(offset + replacement.Length));

		// 只有落在这条记录之后的绝对偏移需要平移；在它之前的（TransformInfo 等）不受影响。
		var threshold = offset + oldLength;
		foreach (var field in HeaderOffsetFields)
			ShiftHeaderOffset(output, field, threshold, delta, diagnostics);

		return (output, ShiftModel(model, threshold, delta));
	}

	/// <summary>量出旧记录的总长度（含 24 字节头与每段之后的 12 字节间隔）。</summary>
	private static int Measure(byte[] data, int offset, List<string> diagnostics)
	{
		var cursor = offset + HeaderSize;
		for (var field = 0; field < 4; field++)
		{
			if (cursor + 4 > data.Length)
			{
				diagnostics.Add("CustomizationInfo 记录被截断（读长度字段越界）。");
				return 0;
			}

			var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4)));
			cursor += 4;
			if (length < 0 || cursor + length > data.Length)
			{
				diagnostics.Add($"CustomizationInfo 第 {field} 段长度 {length} 越界。");
				return 0;
			}

			cursor += length;
			if (field < 3)
				cursor += GapAfterString;
		}

		return cursor - offset;
	}

	/// <summary>按同一套布局序列化新记录。</summary>
	private static byte[] Serialize(UnitCustomizationInfo customization)
	{
		var fields = new[] { customization.BodyType, customization.Slot, customization.Weight, customization.PieceType };
		var size = HeaderSize + fields.Sum(text => 4 + Encoding.UTF8.GetByteCount(text)) + (3 * GapAfterString);
		var data = new byte[size];
		var cursor = HeaderSize;
		for (var i = 0; i < fields.Length; i++)
		{
			var bytes = Encoding.UTF8.GetBytes(fields[i]);
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), checked((uint)bytes.Length));
			cursor += 4;
			bytes.CopyTo(data, cursor);
			cursor += bytes.Length;
			if (i < fields.Length - 1)
				cursor += GapAfterString;
		}

		return data;
	}

	private static void ShiftHeaderOffset(byte[] data, int field, int threshold, int delta, List<string> diagnostics)
	{
		if (field < 0 || field + 4 > data.Length)
			return;
		var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(field, 4));
		if (value == 0 || value < threshold)
			return;
		var shifted = checked((uint)((int)value + delta));
		if (shifted > data.Length)
		{
			diagnostics.Add($"头部偏移 0x{field:x} 平移后越界（{shifted} > {data.Length}）。");
			return;
		}

		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(field, 4), shifted);
	}

	/// <summary>与 <c>CanonicalUnitRebuilder.ShiftOffsets</c> 同构：把模型里记着的绝对偏移一起平移。</summary>
	private static UnitMeshModel ShiftModel(UnitMeshModel model, int threshold, int delta)
	{
		uint Shift(uint value) => value != 0 && value >= threshold ? checked((uint)((int)value + delta)) : value;
		return model with
		{
			UnreversedLodGroupListDataOffset = Shift(model.UnreversedLodGroupListDataOffset),
			CustomizationInfoOffset = Shift(model.CustomizationInfoOffset),
			BoneInfoOffset = Shift(model.BoneInfoOffset),
			StreamInfoOffset = Shift(model.StreamInfoOffset),
			EndingOffset = Shift(model.EndingOffset),
			MeshInfoOffset = Shift(model.MeshInfoOffset),
			MaterialsOffset = Shift(model.MaterialsOffset),
			Streams = model.Streams.Select(stream => stream with { Offset = Shift(stream.Offset) }).ToArray(),
			Meshes = model.Meshes.Select(mesh => mesh with
			{
				Offset = Shift(mesh.Offset),
				MaterialOffset = Shift(mesh.MaterialOffset),
				SectionsOffset = Shift(mesh.SectionsOffset),
				Sections = mesh.Sections.Select(section => section with { Offset = Shift(section.Offset) }).ToArray()
			}).ToArray()
		};
	}
}
