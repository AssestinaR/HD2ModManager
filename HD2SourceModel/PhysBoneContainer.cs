using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HD2SourceModel;

/// <summary>
/// 把 jigglebone 事实序列化成 <c>hd2_spring_rig.bin</c>——运行时兼容层真正驱动用的那份数据。
/// <para>
/// 为什么不直接把 <c>--json</c> 塞进容器：容器是给 C++/Lua 侧的读取器用的，
/// 定长二进制比解析 JSON 便宜得多，而且能明确表达"哪个字段是哪个"。
/// 布局是自描述的：magic + 版本 + 计数 + 名字表偏移，任何一环读错都会立刻暴露。
/// </para>
/// <para>
/// Fixed layout on purpose: the runtime reader is on the other side of a language boundary,
/// so a versioned little-endian block beats parsing JSON every load. Unknown versions must be
/// rejected rather than guessed at.
/// </para>
/// </summary>
public static class PhysBoneSpringRig
{
	/// <summary>承载格式的 magic；读取方必须先校验它。</summary>
	public static ReadOnlySpan<byte> Magic => "HD2SRG01"u8;

	/// <summary>当前布局版本。字段顺序一变就必须加一。</summary>
	/// <remarks>
	/// v2 相对 v1 只加了一件事：每根骨多存**父骨名字**。
	/// 为什么非加不可：链根挂在哪根 HD2 骨上，运行时必须知道；
	/// 而父骨索引是「源模型里的骨下标」，HD2 侧拿不到那张表，只有名字能跨过去。
	/// </remarks>
	public const uint Version = 2;

	private const int HeaderSize = 32;

	/// <summary>每根骨的定长记录：5×u32 + 3×f32 + 29×f32 = 148 字节（v2）。</summary>
	private const int RecordSize = 148;

	/// <summary>序列化整个弹簧 rig。</summary>
	public static byte[] Serialize(IReadOnlyList<SourceJiggleBoneFact> bones)
	{
		ArgumentNullException.ThrowIfNull(bones);

		// 名字表：NUL 结尾的 UTF-8，按骨顺序拼接。空名字也要占一个 NUL，保持偏移可回推。
		var nameTable = new List<byte>();
		var nameOffsets = new int[bones.Count];
		var parentNameOffsets = new int[bones.Count];
		for (var i = 0; i < bones.Count; i++)
		{
			nameOffsets[i] = nameTable.Count;
			nameTable.AddRange(Encoding.UTF8.GetBytes(bones[i].BoneName));
			nameTable.Add(0);
			parentNameOffsets[i] = nameTable.Count;
			nameTable.AddRange(Encoding.UTF8.GetBytes(bones[i].ParentName ?? string.Empty));
			nameTable.Add(0);
		}

		var recordsSize = bones.Count * RecordSize;
		var nameTableOffset = HeaderSize + recordsSize;
		var total = nameTableOffset + nameTable.Count;
		var output = new byte[total];
		var span = output.AsSpan();

		Magic.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Version);
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], checked((uint)bones.Count));
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], checked((uint)nameTableOffset));
		BinaryPrimitives.WriteUInt32LittleEndian(span[20..], checked((uint)nameTable.Count));
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], checked((uint)RecordSize));
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 0); // 保留

		for (var i = 0; i < bones.Count; i++)
		{
			var bone = bones[i];
			var at = HeaderSize + i * RecordSize;
			BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 0)..], checked((uint)bone.BoneIndex));
			BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], bone.ParentBoneIndex);
			BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], checked((uint)nameOffsets[i]));
			BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 12)..], checked((uint)parentNameOffsets[i]));
			BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], checked((uint)bone.Flags));
			BinaryPrimitives.WriteSingleLittleEndian(span[(at + 20)..], (float)bone.WorldPosition.X);
			BinaryPrimitives.WriteSingleLittleEndian(span[(at + 24)..], (float)bone.WorldPosition.Y);
			BinaryPrimitives.WriteSingleLittleEndian(span[(at + 28)..], (float)bone.WorldPosition.Z);
			WriteParameters(span, at + 32, bone.Parameters);
		}

		nameTable.CopyTo(output, nameTableOffset);
		return output;
	}

	/// <summary>29 个参数按 <see cref="SourceJiggleBone"/> 的声明顺序写出。</summary>
	private static void WriteParameters(Span<byte> span, int at, SourceJiggleBone parameters)
	{
		ReadOnlySpan<float> values =
		[
			parameters.Length, parameters.TipMass,
			parameters.YawStiffness, parameters.YawDamping,
			parameters.PitchStiffness, parameters.PitchDamping,
			parameters.AlongStiffness, parameters.AlongDamping,
			parameters.AngleLimit,
			parameters.MinYaw, parameters.MaxYaw, parameters.YawFriction, parameters.YawBounce,
			parameters.MinPitch, parameters.MaxPitch, parameters.PitchFriction, parameters.PitchBounce,
			parameters.BaseMass, parameters.BaseStiffness, parameters.BaseDamping,
			parameters.BaseMinLeft, parameters.BaseMaxLeft, parameters.BaseLeftFriction,
			parameters.BaseMinUp, parameters.BaseMaxUp, parameters.BaseUpFriction,
			parameters.BaseMinForward, parameters.BaseMaxForward, parameters.BaseForwardFriction,
		];
		for (var i = 0; i < values.Length; i++)
			BinaryPrimitives.WriteSingleLittleEndian(span[(at + i * 4)..], values[i]);
	}
}

/// <summary>
/// `HD2PBX01` 嵌入式容器（协议见 <c>docs/hd2physbone-embedded-patch-protocol.md</c>）。
/// <para>
/// 为什么要有这个容器：Patch 的 <c>.gpu_resources</c> 是引擎不认识的 opaque 数据，
/// 把物理数据挂在它末尾就实现了"跟着 mod 走"，不需要额外文件、也不会被引擎解析。
/// 读取方从 EOF 回读 56 字节 footer 找容器起点，因此**不能依赖写入时的绝对偏移**。
/// </para>
/// <para>
/// The container rides at the tail of an opaque carrier so the data travels with the mod and the
/// engine ignores it. Readers locate it from the EOF footer, never from a remembered offset.
/// </para>
/// </summary>
public static class PhysBoneContainer
{
	public static ReadOnlySpan<byte> Magic => "HD2PBX01"u8;
	public static ReadOnlySpan<byte> FooterMagic => "HD2PBFT1"u8;

	public const uint Version = 1;
	private const int HeaderSize = 48;
	private const int FooterSize = 56;
	private const int DigestSize = 32;

	/// <summary>一条命名数据。</summary>
	public readonly record struct Entry(string Name, byte[] Data);

	/// <summary>
	/// 组装一个完整容器（header + 索引 + 数据 + footer）。
	/// </summary>
	public static byte[] Build(IReadOnlyList<Entry> entries, ulong originalPrefixLength = 0)
	{
		ArgumentNullException.ThrowIfNull(entries);

		var indexSize = entries.Sum(entry => 0x38 + Encoding.UTF8.GetByteCount(entry.Name));
		var dataSize = entries.Sum(entry => entry.Data.Length);
		var dataOffset = HeaderSize + indexSize;
		var containerLength = dataOffset + dataSize + FooterSize;

		var output = new byte[containerLength];
		var span = output.AsSpan();

		Magic.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], Version);
		// bit 0：footer 里的 flags 字段保存"容器前的原始前缀长度"。我们在末尾追加，所以前缀就是原文件长度。
		BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], 1u);
		BinaryPrimitives.WriteUInt64LittleEndian(span[0x10..], checked((ulong)containerLength));
		BinaryPrimitives.WriteUInt64LittleEndian(span[0x18..], HeaderSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..], checked((ulong)indexSize));
		BinaryPrimitives.WriteUInt32LittleEndian(span[0x28..], checked((uint)entries.Count));
		BinaryPrimitives.WriteUInt32LittleEndian(span[0x2C..], 0);

		var recordAt = HeaderSize;
		var payloadAt = dataOffset;
		foreach (var entry in entries)
		{
			var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
			BinaryPrimitives.WriteUInt16LittleEndian(span[recordAt..], checked((ushort)nameBytes.Length));
			BinaryPrimitives.WriteUInt16LittleEndian(span[(recordAt + 0x02)..], 0);
			BinaryPrimitives.WriteUInt64LittleEndian(span[(recordAt + 0x04)..], checked((ulong)payloadAt));
			BinaryPrimitives.WriteUInt64LittleEndian(span[(recordAt + 0x0C)..], checked((ulong)entry.Data.Length));
			SHA256.HashData(entry.Data, span[(recordAt + 0x14)..]);
			BinaryPrimitives.WriteUInt32LittleEndian(span[(recordAt + 0x34)..], checked((uint)nameBytes.Length));
			nameBytes.CopyTo(span[(recordAt + 0x38)..]);
			entry.Data.CopyTo(span[payloadAt..]);
			recordAt += 0x38 + nameBytes.Length;
			payloadAt += entry.Data.Length;
		}

		var footerAt = containerLength - FooterSize;
		FooterMagic.CopyTo(span[footerAt..]);
		BinaryPrimitives.WriteUInt64LittleEndian(span[(footerAt + 0x08)..], originalPrefixLength);
		BinaryPrimitives.WriteUInt64LittleEndian(span[(footerAt + 0x10)..], checked((ulong)containerLength));
		// 摘要规则：先把最后 32 字节置零，再对整个容器算 SHA-256。此处它们本来就是零。
		SHA256.HashData(span, span[(footerAt + 0x18)..]);

		return output;
	}

	/// <summary>把容器追加到一个载体文件内容的末尾，返回新的完整内容。</summary>
	public static byte[] AppendTo(byte[] carrier, IReadOnlyList<Entry> entries)
	{
		ArgumentNullException.ThrowIfNull(carrier);
		var container = Build(entries, (ulong)carrier.LongLength);
		var output = new byte[carrier.LongLength + container.LongLength];
		carrier.CopyTo(output, 0);
		container.CopyTo(output, carrier.LongLength);
		return output;
	}

	/// <summary>
	/// 如果载体末尾已经有一个本协议的容器，返回把它去掉之后的内容；否则原样返回。
	/// <para>
	/// 为什么必须有它：同一个输出目录被重复转换（或用户重跑 vpk2patch）时，
	/// 不先去重就会层层叠加，文件越滚越大，而读取方只会看到最后一层。
	/// </para>
	/// <para>
	/// 只认 footer 里记录的容器长度；长度不自洽或 magic 对不上就当作"没有容器"，
	/// 宁可多追加一层（可见、可诊断），也不擅自裁掉别人的数据。
	/// </para>
	/// </summary>
	public static ReadOnlySpan<byte> StripIfPresent(ReadOnlySpan<byte> carrier, out bool stripped)
	{
		stripped = false;
		if (carrier.Length < HeaderSize + FooterSize)
			return carrier;

		var footerAt = carrier.Length - FooterSize;
		if (!carrier.Slice(footerAt, 8).SequenceEqual(FooterMagic))
			return carrier;

		var containerLength = BinaryPrimitives.ReadUInt64LittleEndian(carrier[(footerAt + 0x10)..]);
		if (containerLength < HeaderSize + FooterSize || containerLength > (ulong)carrier.Length)
			return carrier;

		var start = carrier.Length - (int)containerLength;
		if (!carrier.Slice(start, 8).SequenceEqual(Magic))
			return carrier;

		stripped = true;
		return carrier[..start];
	}
}
