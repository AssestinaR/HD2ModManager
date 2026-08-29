using System.Text;

namespace HD2ModCore.Infrastructure;

// 作用：只根据 INI 的实际结构识别 ReShade 预设；不会把任意 INI 或全局 ReShade.ini
// 当作可自动部署的外部组件。
// Purpose: Identifies ReShade presets from their actual INI structure without
// treating arbitrary INI files or the global ReShade.ini as deployable components.
internal static class ReShadeIniClassifier
{
	private const long MaxInspectionBytes = 1 * 1024 * 1024;

	public static bool IsReShadePreset(string path)
	{
		if (!IsIniFile(path) || IsGlobalReShadeConfigurationFile(path)) return false;

		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > MaxInspectionBytes) return false;

			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4096,
				useAsync: false);
			using var reader = new StreamReader(
				stream,
				Encoding.UTF8,
				detectEncodingFromByteOrderMarks: true,
				bufferSize: 4096);

			string? section = null;
			string? line;
			while ((line = reader.ReadLine()) is not null)
			{
				if (line.IndexOf('\0') >= 0) return false;

				var text = line.Trim();
				if (text.Length == 0 || text[0] is ';' or '#') continue;

				if (text[0] == '[')
				{
					if (text.Length < 2 || text[^1] != ']') return false;
					section = text[1..^1].Trim();
					continue;
				}

				if (section is not null) continue;
				var separator = text.IndexOf('=');
				if (separator <= 0) continue;
				var key = text[..separator].Trim();
				if (string.Equals(key, "Techniques", StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
		{
			return false;
		}

		return false;
	}

	private static bool IsIniFile(string path)
		=> path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);

	private static bool IsGlobalReShadeConfigurationFile(string path)
		=> string.Equals(Path.GetFileName(path), "ReShade.ini", StringComparison.OrdinalIgnoreCase);
}
