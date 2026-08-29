using System.Runtime.InteropServices;
using System.Text;

namespace HD2ModAdaptation.PatchReconstruction.Lua;

public sealed record LuaRuntimeValidationResult(
	bool Available,
	bool CompileSucceeded,
	bool RuntimeSucceeded,
	bool EnvironmentLimited,
	string? Message = null);

// Validates the generated chunk with the LuaJIT DLL shipped by the game. A
// clean compile is a hard safety gate; a runtime failure caused by the absent
// in-game `stingray` table is reported as an environment limitation because a
// standalone VM cannot reproduce the loading-screen environment.
public sealed class LuaPatchRuntimeValidator
{
	public ValueTask<LuaRuntimeValidationResult> ValidateAsync(
		string sourceText,
		string luaLibraryPath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sourceText);
		ArgumentException.ThrowIfNullOrWhiteSpace(luaLibraryPath);
		cancellationToken.ThrowIfCancellationRequested();
		if (!File.Exists(luaLibraryPath))
			return ValueTask.FromResult(new LuaRuntimeValidationResult(false, false, false, false, "The game's lua51.dll was not found; runtime validation was skipped."));

		try
		{
			return ValueTask.FromResult(ValidateCore(sourceText, luaLibraryPath));
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or UnauthorizedAccessException or IOException)
		{
			return ValueTask.FromResult(new LuaRuntimeValidationResult(false, false, false, false, exception.Message));
		}
	}

	private static LuaRuntimeValidationResult ValidateCore(string sourceText, string luaLibraryPath)
	{
		var library = NativeLibrary.Load(luaLibraryPath);
		try
		{
			var newState = Load<LuaLNewState>(library, "luaL_newstate");
			var openLibs = Load<LuaLOpenLibs>(library, "luaL_openlibs");
			var loadBuffer = Load<LuaLLoadBuffer>(library, "luaL_loadbuffer");
			var pCall = Load<LuaPCall>(library, "lua_pcall");
			var toString = Load<LuaToString>(library, "lua_tolstring");
			var close = Load<LuaClose>(library, "lua_close");
			var state = newState();
			if (state == IntPtr.Zero) return new LuaRuntimeValidationResult(true, false, false, false, "luaL_newstate returned null.");
			try
			{
				openLibs(state);
				var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
				var sourceBuffer = Marshal.AllocHGlobal(sourceBytes.Length);
				var chunkName = Marshal.StringToHGlobalAnsi("@HD2LuaMergeV1");
				try
				{
					Marshal.Copy(sourceBytes, 0, sourceBuffer, sourceBytes.Length);
					var compileResult = loadBuffer(state, sourceBuffer, (UIntPtr)sourceBytes.Length, chunkName);
					if (compileResult != 0)
						return new LuaRuntimeValidationResult(true, false, false, false, ReadTopString(state, toString));

					var runResult = pCall(state, 0, 0, 0);
					if (runResult == 0)
						return new LuaRuntimeValidationResult(true, true, true, false, null);

					var message = ReadTopString(state, toString);
					var environmentLimited = IsEnvironmentLimitation(message);
					return new LuaRuntimeValidationResult(true, true, false, environmentLimited, message);
				}
				finally
				{
					Marshal.FreeHGlobal(sourceBuffer);
					Marshal.FreeHGlobal(chunkName);
				}
			}
			finally
			{
				close(state);
			}
		}
		finally
		{
			NativeLibrary.Free(library);
		}
	}

	private static T Load<T>(IntPtr library, string name) where T : Delegate
		=> Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

	private static string? ReadTopString(IntPtr state, LuaToString toString)
	{
		var value = toString(state, -1, out var length);
		if (value == IntPtr.Zero || length == UIntPtr.Zero) return null;
		try
		{
			return Marshal.PtrToStringUTF8(value, checked((int)length)) ?? Marshal.PtrToStringAnsi(value);
		}
		catch (ArgumentException)
		{
			return Marshal.PtrToStringAnsi(value);
		}
	}

	private static bool IsEnvironmentLimitation(string? message)
		=> message?.Contains("stingray", StringComparison.OrdinalIgnoreCase) == true
			|| message?.Contains("ffi", StringComparison.OrdinalIgnoreCase) == true
			|| message?.Contains("luaJIT", StringComparison.OrdinalIgnoreCase) == true;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LuaLNewState();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LuaLOpenLibs(IntPtr state);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LuaLLoadBuffer(IntPtr state, IntPtr buffer, UIntPtr size, IntPtr name);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LuaPCall(IntPtr state, int argumentCount, int resultCount, int errorFunction);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LuaToString(IntPtr state, int index, out UIntPtr length);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LuaClose(IntPtr state);
}
