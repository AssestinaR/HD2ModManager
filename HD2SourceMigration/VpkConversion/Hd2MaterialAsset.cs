using System.Buffers.Binary;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// HD2 <c>Material</c> 资产的 TocData 读写。移植自 <c>tmp/rig-lab/hd2_asset.py:96-207</c>
/// （其本身移植自社区 SDK 的 <c>stingray/material.py</c>）。
/// <para>
/// 布局（字节序一律小端）：
/// </para>
/// <code>
/// 12B  undat1
/// u32  EndOffset
/// u64  undat2
/// u64  ParentMaterialId      -> 见 <see cref="KnownParentIds"/>
/// 32B  undat3
/// u32  NumTextures
/// 36B  undat4
/// u32  NumVariables
/// 12B  undat5
/// u32  VariableDataSize
/// 12B  undat6
/// NumTextures × u32 TexUnk   <- 槽位键，**连续一整段**
/// NumTextures × u64 TexId    <- 贴图 id，**紧接着一整段**
/// NumVariables × (u32 klass, u32 elements, u32 id, u32 offset, u32 elementStride)
/// 余下字节 = 着色器变量的值区
/// </code>
/// <para>
/// <b>贴图表是两段连续数组，不是 N 条交错的 (u32, u64) 记录。</b>
/// 读成交错会把除最后一条以外的所有 id 全部错位，而<b>自洽的读写回环检测不出来</b>
/// —— Python 侧把那个错误版本冻结成负面对照留档（<c>_legacy_matwriter.py</c>，
/// 其抬头明说"只有外部权威能暴露它"）。这里的写法与
/// <c>StingrayMaterialReferenceReader.cs</c> 的 <c>textureIdsOffset = 136 + numTextures*4</c> 一致。
/// </para>
/// </summary>
public sealed class Hd2MaterialAsset
{
	/// <summary>Texture 表的键区起点。值区在 <c>136 + 12*NumTextures</c>，与上面的注释同源。</summary>
	public const int TextureKeyOffset = 136;

	private const int HeaderSize = 136;
	private const int VariableRecordSize = 20;

	/// <summary>
	/// SDK 的 <c>Global_MaterialParentIDs</c>：模板 id → 名字。新造材质时必须从这里挑。
	/// <para>
	/// <b>这张表是 Python 侧从 SDK <c>constants.py</c> 抄来的，并不完整。</b>
	/// 仓库自带的模板当天就抓出了缺口：<c>alphaclip+</c> 用的是
	/// <c>0xc6042e3403385d40</c>，不在原表里。所以这里是<b>实测过的完整集合</b>
	/// （由 <c>Hd2MaterialTemplateTests.EveryTemplateDeclaresAKnownGlobalParent</c> 守住）；
	/// 换 SDK 版本后若新增模板报未知父类，按报出来的值补进来即可。
	/// </para>
	/// </summary>
	public static readonly IReadOnlyDictionary<ulong, string> KnownParentIds = new Dictionary<ulong, string>
	{
		[3430705909399566334] = "basic+",
		[15586118709890920288] = "alphaclip",
		[6101987038150196875] = "original",
		[15356477064658408677] = "basic",
		[15235712479575174153] = "emissive",
		[17265463703140804126] = "advanced",
		[17720495965476876300] = "armorlut",
		[9576304397847579354] = "translucent",
		[8580182439406660688] = "basic+",
		[0xc6042e3403385d40] = "alphaclip+",
	};

	public byte[] Undat1 { get; set; } = new byte[12];

	/// <summary>
	/// <b>原样保留，绝不重算。</b> SDK（<c>stingray/material.py</c>）把它当不透明 u32 从不改写，
	/// 真实文件也证明它不是 <c>136 + 12*ntex + 20*nvar</c>：SDK 模板是 <c>size-36</c>，
	/// 而出货的 Chiffon 材质 <c>0x0228cdef42ee1e6e</c> 是 <c>size-32</c>。
	/// 用公式重算会静默改坏头部后面那一个字节。
	/// </summary>
	public uint EndOffset { get; set; }

	public ulong Undat2 { get; set; }

	public ulong ParentMaterialId { get; set; }

	public byte[] Undat3 { get; set; } = new byte[32];

	public byte[] Undat4 { get; set; } = new byte[36];

	public byte[] Undat5 { get; set; } = new byte[12];

	public byte[] Undat6 { get; set; } = new byte[12];

	public uint VariableDataSize { get; set; }

	/// <summary>槽位键（每槽一个 u32）。与 <see cref="TextureIds"/> 一一对应。</summary>
	public IReadOnlyList<uint> TextureKeys { get; set; } = [];

	/// <summary>贴图 id（每槽一个 u64）。0 = 空槽。</summary>
	public IReadOnlyList<ulong> TextureIds { get; set; } = [];

	public IReadOnlyList<Hd2ShaderVariable> Variables { get; set; } = [];

	/// <summary>变量值区的原始字节。写出时保留，只按变量覆盖各自那几段浮点。</summary>
	public byte[] ValueArea { get; set; } = [];

	public string ParentName => KnownParentIds.TryGetValue(ParentMaterialId, out var name)
		? name
		: $"0x{ParentMaterialId:x16}";

	public static Hd2MaterialAsset Read(ReadOnlySpan<byte> data)
	{
		if (data.Length < HeaderSize)
			throw new InvalidDataException($"Material TocData 只有 {data.Length} 字节，头部就要 {HeaderSize}。");

		var asset = new Hd2MaterialAsset
		{
			Undat1 = data[..12].ToArray(),
			EndOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
			Undat2 = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
			ParentMaterialId = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
			Undat3 = data[32..64].ToArray(),
		};

		var cursor = 64;
		var textureCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]));
		cursor += 4;
		asset.Undat4 = data[cursor..(cursor + 36)].ToArray();
		cursor += 36;
		var variableCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]));
		cursor += 4;
		asset.Undat5 = data[cursor..(cursor + 12)].ToArray();
		cursor += 12;
		asset.VariableDataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
		cursor += 4;
		asset.Undat6 = data[cursor..(cursor + 12)].ToArray();
		cursor += 12;
		if (cursor != HeaderSize)
			throw new InvalidDataException($"Material 头部游标算到 {cursor}，与常量 {HeaderSize} 不一致。");

		var keys = new uint[textureCount];
		for (var i = 0; i < textureCount; i++)
			keys[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + (4 * i))..]);
		cursor += 4 * textureCount;

		var ids = new ulong[textureCount];
		for (var i = 0; i < textureCount; i++)
			ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(data[(cursor + (8 * i))..]);
		cursor += 8 * textureCount;

		var variables = new Hd2ShaderVariable[variableCount];
		for (var i = 0; i < variableCount; i++)
		{
			var record = data[(cursor + (VariableRecordSize * i))..];
			variables[i] = new Hd2ShaderVariable
			{
				Class = BinaryPrimitives.ReadUInt32LittleEndian(record),
				Elements = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
				Id = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
				Offset = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
				ElementStride = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
			};
		}

		cursor += VariableRecordSize * variableCount;
		var valueArea = data[cursor..].ToArray();
		foreach (var variable in variables)
		{
			var values = new float[variable.FloatCount];
			for (var k = 0; k < values.Length; k++)
			{
				// 注意 uint + int 会提升成 long，偏移一律显式转 int。
				var at = (int)variable.Offset + (4 * k);
				values[k] = at + 4 <= valueArea.Length
					? BinaryPrimitives.ReadSingleLittleEndian(valueArea.AsSpan(at, 4))
					: 0f;
			}

			variable.Values = values;
		}

		asset.TextureKeys = keys;
		asset.TextureIds = ids;
		asset.Variables = variables;
		asset.ValueArea = valueArea;
		return asset;
	}

	public byte[] Write()
	{
		if (TextureKeys.Count != TextureIds.Count)
			throw new InvalidDataException($"TexUnk（{TextureKeys.Count}）与 TexId（{TextureIds.Count}）必须一一对应。");

		var variablesSize = VariableRecordSize * Variables.Count;
		var valueAreaSize = ValueArea.Length;
		foreach (var variable in Variables)
			valueAreaSize = Math.Max(valueAreaSize, (int)variable.Offset + (4 * variable.FloatCount));

		var output = new byte[HeaderSize + (4 * TextureKeys.Count) + (8 * TextureIds.Count) + variablesSize + valueAreaSize];
		var span = output.AsSpan();

		Undat1.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], EndOffset);
		BinaryPrimitives.WriteUInt64LittleEndian(span[16..], Undat2);
		BinaryPrimitives.WriteUInt64LittleEndian(span[24..], ParentMaterialId);
		Undat3.CopyTo(span[32..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[64..], checked((uint)TextureIds.Count));
		Undat4.CopyTo(span[68..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[104..], checked((uint)Variables.Count));
		Undat5.CopyTo(span[108..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[120..], VariableDataSize);
		Undat6.CopyTo(span[124..]);

		var cursor = HeaderSize;
		// 写出时同样保持"两段连续数组"：先全部键，再全部 id。
		foreach (var key in TextureKeys)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], key);
			cursor += 4;
		}

		foreach (var id in TextureIds)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(span[cursor..], id);
			cursor += 8;
		}

		foreach (var variable in Variables)
		{
			var record = span[cursor..];
			BinaryPrimitives.WriteUInt32LittleEndian(record, variable.Class);
			BinaryPrimitives.WriteUInt32LittleEndian(record[4..], variable.Elements);
			BinaryPrimitives.WriteUInt32LittleEndian(record[8..], variable.Id);
			BinaryPrimitives.WriteUInt32LittleEndian(record[12..], variable.Offset);
			BinaryPrimitives.WriteUInt32LittleEndian(record[16..], variable.ElementStride);
			cursor += VariableRecordSize;
		}

		// 值区：先铺原始字节，再按每个变量自己的 offset 覆盖浮点——保持 offset 逐字节不变。
		ValueArea.CopyTo(span[cursor..]);
		var valueSpan = span[cursor..];
		foreach (var variable in Variables)
		{
			for (var k = 0; k < variable.Values.Count; k++)
				BinaryPrimitives.WriteSingleLittleEndian(valueSpan[((int)variable.Offset + (4 * k))..], variable.Values[k]);
		}

		return output;
	}

	/// <summary>
	/// 替换一个<b>已存在</b>槽位的贴图 id。
	/// <para>
	/// 刻意严格：槽位键属于模板的着色器契约，凭空造一个槽（追加零键）会产出游戏解析不了的材质。
	/// 需要新槽时应当换一个槽表里本来就有该槽的模板。
	/// </para>
	/// </summary>
	public void SetTexture(int index, ulong textureId)
	{
		if (index < 0 || index >= TextureIds.Count)
			throw new ArgumentOutOfRangeException(
				nameof(index),
				$"槽 {index} 超出模板 {ParentName} 的 {TextureIds.Count} 个槽。");
		var ids = TextureIds.ToArray();
		ids[index] = textureId;
		TextureIds = ids;
	}
}

/// <summary>材质里的一个着色器变量。</summary>
public sealed class Hd2ShaderVariable
{
	public uint Class { get; set; }

	public uint Elements { get; set; }

	public uint Id { get; set; }

	/// <summary>值区内的字节偏移。</summary>
	public uint Offset { get; set; }

	public uint ElementStride { get; set; }

	public IReadOnlyList<float> Values { get; set; } = [];

	/// <summary>SDK 约定：类号 + 1 就是浮点个数（Scalar=1、Vector4=4）。</summary>
	public int FloatCount => checked((int)Class + 1);

	public string ClassName => Class switch
	{
		0 => "Scalar",
		1 => "Vector2",
		2 => "Vector3",
		3 => "Vector4",
		_ => "Other",
	};
}
