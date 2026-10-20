using System.Buffers.Binary;
using System.Text;
using HD2ModAdaptation.PatchReconstruction.Lua;
using Xunit;

namespace HD2ModAdaptation.Tests;

// The emitted Lua resource has to be byte-for-byte the shape the game ships:
// an eight-byte prefix and then PUC-Lua 5.1 bytecode. These tests pin the writer
// against the reader so a change to either side cannot silently drift.
public sealed class Lua51BytecodeTests
{
	private const int OpGetGlobal = 5;
	private const int OpLoadK = 1;
	private const int OpCall = 28;
	private const int OpTest = 26;
	private const int OpJump = 22;
	private const int OpReturn = 30;

	[Fact]
	public void WrapperChunk_StartsWithLua51SignatureAndRoundTrips()
	{
		var chunk = Lua51WrapperChunk.Build("print('hello')");

		Assert.Equal(new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a' }, chunk[..4]);
		Assert.Equal(0x51, chunk[4]);
		Assert.Equal(0, chunk[5]);
		Assert.Equal(1, chunk[6]);
		// int, size_t, Instruction, lua_Number sizes and the integral flag.
		Assert.Equal(new byte[] { 4, 4, 4, 8, 0 }, chunk[7..12]);

		var parsed = Lua51Bytecode.Read(chunk);
		Assert.Equal(chunk, Lua51Bytecode.Write(parsed));
		Assert.Equal(4, parsed.Main.MaxStackSize);
		Assert.Equal(2, parsed.Main.IsVararg);
		Assert.Equal(0, parsed.Main.ParameterCount);
		Assert.Empty(parsed.Main.Prototypes);
		Assert.Equal(5, parsed.Main.Constants.Count);
		Assert.Equal("rawget", Encoding.UTF8.GetString(parsed.Main.Constants[0].Text!));
		Assert.Equal("_G", Encoding.UTF8.GetString(parsed.Main.Constants[1].Text!));
		Assert.Equal("loadstring", Encoding.UTF8.GetString(parsed.Main.Constants[2].Text!));
		Assert.Equal("load", Encoding.UTF8.GetString(parsed.Main.Constants[3].Text!));
		Assert.Equal("print('hello')", Encoding.UTF8.GetString(parsed.Main.Constants[4].Text!));
	}

	[Fact]
	public void WrapperChunk_EmitsTheExpectedProgram()
	{
		var parsed = Lua51Bytecode.Read(Lua51WrapperChunk.Build("return 1"));
		var code = parsed.Main.Code;

		// local f = rawget(_G, "loadstring") or rawget(_G, "load")
		// if f then return f(source)() end
		// return
		Assert.Equal(16, code.Count);
		Assert.Equal(OpGetGlobal, Opcode(code[0]));
		Assert.Equal(0, ArgBx(code[0]));
		Assert.Equal(OpGetGlobal, Opcode(code[1]));
		Assert.Equal(1, ArgBx(code[1]));
		Assert.Equal(OpLoadK, Opcode(code[2]));
		Assert.Equal(2, ArgBx(code[2]));
		Assert.Equal(OpCall, Opcode(code[3]));
		Assert.Equal(3, ArgB(code[3]));
		Assert.Equal(2, ArgC(code[3]));

		// TEST with C=1 jumps when the value is truthy: a present loadstring skips
		// the `load` fallback, so the jump has to land on the LOADK that follows it.
		Assert.Equal(OpTest, Opcode(code[4]));
		Assert.Equal(1, ArgC(code[4]));
		Assert.Equal(OpJump, Opcode(code[5]));
		Assert.Equal(9, 6 + ArgSBx(code[5]));

		// `load` fallback: same shape, and it skips the call when it is nil too.
		Assert.Equal(OpGetGlobal, Opcode(code[6]));
		Assert.Equal(3, ArgBx(code[6]));
		Assert.Equal(OpTest, Opcode(code[7]));
		Assert.Equal(1, ArgC(code[7]));
		Assert.Equal(OpJump, Opcode(code[8]));
		Assert.Equal(11, 9 + ArgSBx(code[8]));

		Assert.Equal(OpLoadK, Opcode(code[9]));
		Assert.Equal(1, ArgA(code[9]));
		Assert.Equal(4, ArgBx(code[9]));
		Assert.Equal(OpCall, Opcode(code[10]));
		Assert.Equal(2, ArgB(code[10]));
		Assert.Equal(2, ArgC(code[10]));

		Assert.Equal(OpTest, Opcode(code[11]));
		Assert.Equal(0, ArgC(code[11]));
		Assert.Equal(OpJump, Opcode(code[12]));
		Assert.Equal(15, 13 + ArgSBx(code[12]));

		Assert.Equal(OpCall, Opcode(code[13]));
		Assert.Equal(1, ArgB(code[13]));
		Assert.Equal(0, ArgC(code[13]));
		Assert.Equal(OpReturn, Opcode(code[14]));
		Assert.Equal(0, ArgB(code[14]));
		Assert.Equal(OpReturn, Opcode(code[15]));
		Assert.Equal(1, ArgB(code[15]));
	}

	[Fact]
	public void Read_RejectsForeignBytecode()
	{
		var luaJit = new byte[] { 0x1B, (byte)'L', (byte)'J', 0x02, 0x01, 0, 0, 0, 0, 0, 0, 0 };
		var error = Assert.Throws<LuaPatchFormatException>(() => Lua51Bytecode.Read(luaJit));
		Assert.Contains("5.1", error.Message, StringComparison.Ordinal);

		var truncated = new byte[] { 0x1B, (byte)'L', (byte)'u', (byte)'a' };
		Assert.Throws<LuaPatchFormatException>(() => Lua51Bytecode.Read(truncated));
	}

	[Fact]
	public void TryUnwrapResource_AcceptsBothKnownVersions()
	{
		var chunk = Lua51WrapperChunk.Build("return 1");
		foreach (var version in new[] { LuaResourceFormat.BytecodeVersion, LuaResourceFormat.LegacySourceVersion })
		{
			var resource = new byte[8 + chunk.Length];
			BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(0, 4), checked((uint)chunk.Length));
			BinaryPrimitives.WriteUInt32LittleEndian(resource.AsSpan(4, 4), version);
			chunk.CopyTo(resource.AsSpan(8));

			Assert.True(Lua51Bytecode.TryUnwrapResource(resource, out var readVersion, out var payload));
			Assert.Equal(version, readVersion);
			Assert.Equal(chunk, payload.ToArray());
		}

		// A length that disagrees with the resource is how a stale or corrupt
		// resource announces itself; it must never be treated as loadable.
		var broken = new byte[16];
		BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(0, 4), 999);
		Assert.False(Lua51Bytecode.TryUnwrapResource(broken, out _, out _));
	}

	private static int Opcode(uint instruction) => (int)(instruction & 0x3F);
	private static int ArgA(uint instruction) => (int)((instruction >> 6) & 0xFF);
	private static int ArgC(uint instruction) => (int)((instruction >> 14) & 0x1FF);
	private static int ArgB(uint instruction) => (int)((instruction >> 23) & 0x1FF);
	private static int ArgBx(uint instruction) => (int)(instruction >> 14);
	private static int ArgSBx(uint instruction) => ArgBx(instruction) - 131071;
}
