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
		Assert.Equal(2, parsed.Main.MaxStackSize);
		Assert.Equal(2, parsed.Main.IsVararg);
		Assert.Equal(0, parsed.Main.ParameterCount);
		Assert.Empty(parsed.Main.Prototypes);
		Assert.Equal(2, parsed.Main.Constants.Count);
		Assert.Equal("load", Encoding.UTF8.GetString(parsed.Main.Constants[0].Text!));
		Assert.Equal("print('hello')", Encoding.UTF8.GetString(parsed.Main.Constants[1].Text!));
	}

	[Fact]
	public void WrapperChunk_EmitsTheExpectedProgram()
	{
		var parsed = Lua51Bytecode.Read(Lua51WrapperChunk.Build("return 1"));
		var code = parsed.Main.Code;

		// local f = load(source); if f then return f() end; return
		Assert.Equal(8, code.Count);
		Assert.Equal(OpGetGlobal, Opcode(code[0]));
		Assert.Equal(0, ArgA(code[0]));
		Assert.Equal(0, ArgBx(code[0]));
		Assert.Equal(OpLoadK, Opcode(code[1]));
		Assert.Equal(1, ArgA(code[1]));
		Assert.Equal(1, ArgBx(code[1]));
		Assert.Equal(OpCall, Opcode(code[2]));
		Assert.Equal(2, ArgB(code[2]));
		Assert.Equal(2, ArgC(code[2]));
		// TEST falls through when R0 is truthy and takes the following JMP when it
		// is falsy, so the JMP has to land on the final plain return.
		Assert.Equal(OpTest, Opcode(code[3]));
		Assert.Equal(0, ArgC(code[3]));
		Assert.Equal(OpJump, Opcode(code[4]));
		Assert.Equal(7, 5 + ArgSBx(code[4]));
		Assert.Equal(OpCall, Opcode(code[5]));
		Assert.Equal(1, ArgB(code[5]));
		Assert.Equal(0, ArgC(code[5]));
		Assert.Equal(OpReturn, Opcode(code[6]));
		Assert.Equal(0, ArgB(code[6]));
		Assert.Equal(OpReturn, Opcode(code[7]));
		Assert.Equal(1, ArgB(code[7]));
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
