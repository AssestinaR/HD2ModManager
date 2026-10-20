using System.Buffers.Binary;
using System.Text;

namespace HD2ModAdaptation.PatchReconstruction.Lua;

// The game's Lua entry point is no longer a source chunk. Since the 2026-10-20
// update the boot resource is an eight-byte prefix (payload length, format
// version 1) followed by stock PUC-Lua 5.1 bytecode, and every source-text Lua
// mod now crashes the game. The manager therefore has to emit the same shape it
// receives: bytecode. Keeping a reader as well as a writer is what makes that
// safe -- the reader round-trips the game's own resource byte for byte, so the
// writer is checked against real data instead of against our assumptions.
public sealed class Lua51Chunk
{
	public Lua51Chunk(byte[] header, Lua51Prototype main)
	{
		Header = header;
		Main = main;
	}

	/// <summary>Raw twelve-byte chunk header, preserved so a rewrite cannot drift.</summary>
	public byte[] Header { get; }

	public Lua51Prototype Main { get; }
}

public sealed class Lua51Prototype
{
	public byte[]? SourceName { get; init; }
	public int LineDefined { get; init; }
	public int LastLineDefined { get; init; }
	public byte UpvalueCount { get; init; }
	public byte ParameterCount { get; init; }
	public byte IsVararg { get; init; }
	public byte MaxStackSize { get; init; }
	public IReadOnlyList<uint> Code { get; init; } = Array.Empty<uint>();
	public IReadOnlyList<Lua51Constant> Constants { get; init; } = Array.Empty<Lua51Constant>();
	public IReadOnlyList<Lua51Prototype> Prototypes { get; init; } = Array.Empty<Lua51Prototype>();
	public IReadOnlyList<int> LineInfo { get; init; } = Array.Empty<int>();
	public IReadOnlyList<Lua51Local> Locals { get; init; } = Array.Empty<Lua51Local>();
	public IReadOnlyList<byte[]?> UpvalueNames { get; init; } = Array.Empty<byte[]?>();
}

/// <summary>
/// One entry of a prototype's constant table. Numbers are kept as the raw
/// eight bytes on disk so re-serialisation is bit exact even for values whose
/// double representation is not round-trippable through text.
/// </summary>
public readonly record struct Lua51Constant(byte Type, ulong RawNumber, byte[]? Text, bool Boolean)
{
	public const byte TypeNil = 0;
	public const byte TypeBoolean = 1;
	public const byte TypeNumber = 3;
	public const byte TypeString = 4;

	public static Lua51Constant Nil => new(TypeNil, 0, null, false);
	public static Lua51Constant FromString(byte[] text) => new(TypeString, 0, text, false);
	public static Lua51Constant FromString(string text) => FromString(Encoding.UTF8.GetBytes(text));
	public static Lua51Constant FromNumber(double value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
		return new Lua51Constant(TypeNumber, BinaryPrimitives.ReadUInt64LittleEndian(buffer), null, false);
	}

	public double AsDouble()
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(buffer, RawNumber);
		return BinaryPrimitives.ReadDoubleLittleEndian(buffer);
	}
}

public readonly record struct Lua51Local(byte[]? Name, int StartPc, int EndPc);

public static class Lua51Bytecode
{
	public const int HeaderLength = 12;
	private const byte Lua51Version = 0x51;
	private const int MaxNestingDepth = 64;

	public static Lua51Chunk Read(ReadOnlySpan<byte> data)
	{
		if (data.Length < HeaderLength)
			throw new LuaPatchFormatException("Lua 5.1 chunk is shorter than its own header.");
		if (data[0] != 0x1B || data[1] != (byte)'L' || data[2] != (byte)'u' || data[3] != (byte)'a')
			throw new LuaPatchFormatException("Lua 5.1 chunk is missing the \\27Lua signature.");
		if (data[4] != Lua51Version)
			throw new LuaPatchFormatException($"Unsupported Lua bytecode version 0x{data[4]:x2}; only 5.1 is supported.");
		if (data[5] != 0)
			throw new LuaPatchFormatException("Unsupported Lua bytecode format byte.");
		if (data[6] != 1)
			throw new LuaPatchFormatException("Only little-endian Lua bytecode is supported.");
		if (data[7] != 4 || data[8] != 4 || data[9] != 4 || data[10] != 8 || data[11] != 0)
			throw new LuaPatchFormatException("Unsupported Lua 5.1 numeric configuration.");

		var reader = new Reader(data, HeaderLength);
		var main = ReadPrototype(ref reader, 0);
		if (reader.Position != data.Length)
			throw new LuaPatchFormatException($"Lua 5.1 chunk carries {data.Length - reader.Position} trailing bytes after the main prototype (consumed {reader.Position} of {data.Length}).");
		return new Lua51Chunk(data[..HeaderLength].ToArray(), main);
	}

	public static byte[] Write(Lua51Chunk chunk)
	{
		ArgumentNullException.ThrowIfNull(chunk);
		if (chunk.Header.Length != HeaderLength)
			throw new LuaPatchFormatException("Lua 5.1 chunk header must be exactly twelve bytes.");
		var output = new List<byte>(4096);
		output.AddRange(chunk.Header);
		WritePrototype(output, chunk.Main);
		return output.ToArray();
	}

	/// <summary>
	/// Reads a whole resource prefix and returns the bytecode it wraps. The prefix
	/// is a payload length and a format version; version 1 is the bytecode format
	/// the game ships today, version 2 was the source-text format used before the
	/// 2026-10-20 update.
	/// </summary>
	public static bool TryUnwrapResource(ReadOnlySpan<byte> resource, out uint version, out ReadOnlySpan<byte> payload)
	{
		version = 0;
		payload = default;
		if (resource.Length < 8) return false;
		var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(resource[..4]);
		if (declaredLength != resource.Length - 8) return false;
		version = BinaryPrimitives.ReadUInt32LittleEndian(resource.Slice(4, 4));
		payload = resource[8..];
		return true;
	}

	private static Lua51Prototype ReadPrototype(ref Reader reader, int depth)
	{
		if (depth > MaxNestingDepth)
			throw new LuaPatchFormatException("Lua 5.1 chunk nests functions too deeply.");

		var sourceName = reader.ReadString();
		var lineDefined = reader.ReadInt32();
		var lastLineDefined = reader.ReadInt32();
		var upvalueCount = reader.ReadByte();
		var parameterCount = reader.ReadByte();
		var isVararg = reader.ReadByte();
		var maxStackSize = reader.ReadByte();

		var codeCount = reader.ReadCount("instruction");
		var code = new uint[codeCount];
		for (var index = 0; index < codeCount; index++) code[index] = reader.ReadUInt32();

		var constantCount = reader.ReadCount("constant");
		var constants = new Lua51Constant[constantCount];
		for (var index = 0; index < constantCount; index++)
		{
			var type = reader.ReadByte();
			constants[index] = type switch
			{
				Lua51Constant.TypeNil => Lua51Constant.Nil,
				Lua51Constant.TypeBoolean => new Lua51Constant(type, 0, null, reader.ReadByte() != 0),
				Lua51Constant.TypeNumber => new Lua51Constant(type, reader.ReadUInt64(), null, false),
				Lua51Constant.TypeString => new Lua51Constant(type, 0, reader.ReadString(), false),
				_ => throw new LuaPatchFormatException($"Unsupported Lua constant type {type}."),
			};
		}

		var prototypeCount = reader.ReadCount("prototype");
		var prototypes = new Lua51Prototype[prototypeCount];
		for (var index = 0; index < prototypeCount; index++) prototypes[index] = ReadPrototype(ref reader, depth + 1);

		var lineInfoCount = reader.ReadCount("line info");
		var lineInfo = new int[lineInfoCount];
		for (var index = 0; index < lineInfoCount; index++) lineInfo[index] = reader.ReadInt32();

		var localCount = reader.ReadCount("local variable");
		var locals = new Lua51Local[localCount];
		for (var index = 0; index < localCount; index++)
			locals[index] = new Lua51Local(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32());

		var upvalueNameCount = reader.ReadCount("upvalue name");
		var upvalueNames = new byte[]?[upvalueNameCount];
		for (var index = 0; index < upvalueNameCount; index++) upvalueNames[index] = reader.ReadString();

		return new Lua51Prototype
		{
			SourceName = sourceName,
			LineDefined = lineDefined,
			LastLineDefined = lastLineDefined,
			UpvalueCount = upvalueCount,
			ParameterCount = parameterCount,
			IsVararg = isVararg,
			MaxStackSize = maxStackSize,
			Code = code,
			Constants = constants,
			Prototypes = prototypes,
			LineInfo = lineInfo,
			Locals = locals,
			UpvalueNames = upvalueNames,
		};
	}

	private static void WritePrototype(List<byte> output, Lua51Prototype prototype)
	{
		WriteString(output, prototype.SourceName);
		WriteInt32(output, prototype.LineDefined);
		WriteInt32(output, prototype.LastLineDefined);
		output.Add(prototype.UpvalueCount);
		output.Add(prototype.ParameterCount);
		output.Add(prototype.IsVararg);
		output.Add(prototype.MaxStackSize);

		WriteInt32(output, prototype.Code.Count);
		foreach (var instruction in prototype.Code) WriteUInt32(output, instruction);

		WriteInt32(output, prototype.Constants.Count);
		foreach (var constant in prototype.Constants)
		{
			output.Add(constant.Type);
			switch (constant.Type)
			{
				case Lua51Constant.TypeNil:
					break;
				case Lua51Constant.TypeBoolean:
					output.Add(constant.Boolean ? (byte)1 : (byte)0);
					break;
				case Lua51Constant.TypeNumber:
					WriteUInt64(output, constant.RawNumber);
					break;
				case Lua51Constant.TypeString:
					WriteString(output, constant.Text);
					break;
				default:
					throw new LuaPatchFormatException($"Unsupported Lua constant type {constant.Type}.");
			}
		}

		WriteInt32(output, prototype.Prototypes.Count);
		foreach (var child in prototype.Prototypes) WritePrototype(output, child);

		WriteInt32(output, prototype.LineInfo.Count);
		foreach (var line in prototype.LineInfo) WriteInt32(output, line);

		WriteInt32(output, prototype.Locals.Count);
		foreach (var local in prototype.Locals)
		{
			WriteString(output, local.Name);
			WriteInt32(output, local.StartPc);
			WriteInt32(output, local.EndPc);
		}

		WriteInt32(output, prototype.UpvalueNames.Count);
		foreach (var name in prototype.UpvalueNames) WriteString(output, name);
	}

	// luaU_dump stores a string as its length including the terminating NUL,
	// followed by the bytes and that NUL. A zero length means "no string".
	private static void WriteString(List<byte> output, byte[]? value)
	{
		if (value is null)
		{
			WriteInt32(output, 0);
			return;
		}
		WriteInt32(output, value.Length + 1);
		output.AddRange(value);
		output.Add(0);
	}

	private static void WriteInt32(List<byte> output, int value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
		foreach (var item in buffer) output.Add(item);
	}

	private static void WriteUInt32(List<byte> output, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
		foreach (var item in buffer) output.Add(item);
	}

	private static void WriteUInt64(List<byte> output, ulong value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
		foreach (var item in buffer) output.Add(item);
	}

	private ref struct Reader(ReadOnlySpan<byte> data, int position)
	{
		private readonly ReadOnlySpan<byte> _data = data;

		public int Position { get; private set; } = position;

		public byte ReadByte()
		{
			Ensure(1);
			return _data[Position++];
		}

		public int ReadInt32()
		{
			Ensure(4);
			var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(Position, 4));
			Position += 4;
			return value;
		}

		public uint ReadUInt32()
		{
			Ensure(4);
			var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Position, 4));
			Position += 4;
			return value;
		}

		public ulong ReadUInt64()
		{
			Ensure(8);
			var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(Position, 8));
			Position += 8;
			return value;
		}

		public int ReadCount(string what)
		{
			var count = ReadInt32();
			if (count < 0 || count > _data.Length)
				throw new LuaPatchFormatException($"Lua 5.1 chunk declares an impossible {what} count: {count}.");
			return count;
		}

		public byte[]? ReadString()
		{
			var size = ReadInt32();
			if (size == 0) return null;
			if (size < 0) throw new LuaPatchFormatException($"Lua 5.1 chunk declares a negative string length: {size}.");
			Ensure(size);
			var value = _data.Slice(Position, size - 1).ToArray();
			Position += size;
			return value;
		}

		private void Ensure(int count)
		{
			if (count < 0 || Position + count > _data.Length)
				throw new LuaPatchFormatException("Lua 5.1 chunk is truncated.");
		}
	}
}

// Builds the single chunk the manager now emits. Instead of a whole compiler this
// is one fixed, provably correct prototype equivalent to
//
//     local f = load(<source>)
//     if f then return f() end
//     return
//
// so the merged source text keeps working while the payload becomes bytecode.
public static class Lua51WrapperChunk
{
	private const int OpTest = 26;
	private const int OpJump = 22;
	private const int OpCall = 28;
	private const int OpReturn = 30;
	private const int OpLoadK = 1;
	private const int OpGetGlobal = 5;
	private const int MaxArgSBx = 131071;
	private const int VarargIsVararg = 2;

	public static byte[] Build(string sourceText)
	{
		ArgumentNullException.ThrowIfNull(sourceText);
		var source = Encoding.UTF8.GetBytes(sourceText);
		var loaderName = Lua51Constant.FromString("load");

		// R0 = load, R1 = source text; both fit in two stack slots.
		var code = new[]
		{
			EncodeABx(OpGetGlobal, 0, 0),
			EncodeABx(OpLoadK, 1, 1),
			EncodeABC(OpCall, 0, 2, 2),
			EncodeABC(OpTest, 0, 0, 0),
			EncodeASBx(OpJump, 0, 2),
			EncodeABC(OpCall, 0, 1, 0),
			EncodeABC(OpReturn, 0, 0, 0),
			EncodeABC(OpReturn, 0, 1, 0),
		};

		var main = new Lua51Prototype
		{
			SourceName = null,
			LineDefined = 0,
			LastLineDefined = 0,
			UpvalueCount = 0,
			ParameterCount = 0,
			IsVararg = VarargIsVararg,
			MaxStackSize = 2,
			Code = code,
			Constants = [loaderName, Lua51Constant.FromString(source)],
		};

		var header = new byte[Lua51Bytecode.HeaderLength];
		header[0] = 0x1B;
		header[1] = (byte)'L';
		header[2] = (byte)'u';
		header[3] = (byte)'a';
		header[4] = 0x51;
		header[5] = 0;
		header[6] = 1;
		header[7] = 4;
		header[8] = 4;
		header[9] = 4;
		header[10] = 8;
		header[11] = 0;
		return Lua51Bytecode.Write(new Lua51Chunk(header, main));
	}

	private static uint EncodeABC(int opcode, int a, int b, int c)
		=> unchecked((uint)(opcode | (a << 6) | (c << 14) | (b << 23)));

	private static uint EncodeABx(int opcode, int a, int bx)
		=> unchecked((uint)(opcode | (a << 6) | (bx << 14)));

	private static uint EncodeASBx(int opcode, int a, int sbx)
		=> EncodeABx(opcode, a, sbx + MaxArgSBx);
}
