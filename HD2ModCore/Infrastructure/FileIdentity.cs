using System.Runtime.InteropServices;

namespace HD2ModCore.Infrastructure;

// 作用：判断两个路径是否是同一个磁盘文件（NTFS 硬链接的两个名字文件 ID 相同），不读文件内容。
// Purpose: Tells whether two paths are one file on disk (two names of an NTFS hard link share volume serial and
// file index) without reading any content.
internal static class FileIdentity
{
	// true: same file; false: different files; null: the answer could not be determined.
	public static bool? AreSameFile(string first, string second)
	{
		if (!OperatingSystem.IsWindows()) return null;
		var a = TryRead(first);
		var b = TryRead(second);
		if (a is null || b is null) return null;
		return a.Value == b.Value;
	}

	private static (uint Volume, uint High, uint Low)? TryRead(string path)
	{
		try
		{
			using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			return GetFileInformationByHandle(handle.DangerousGetHandle(), out var info)
				? (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow)
				: null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ByHandleFileInformation
	{
		public uint FileAttributes;
		public uint CreationTimeLow, CreationTimeHigh;
		public uint LastAccessTimeLow, LastAccessTimeHigh;
		public uint LastWriteTimeLow, LastWriteTimeHigh;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh, FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh, FileIndexLow;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandle(IntPtr file, out ByHandleFileInformation information);
}
