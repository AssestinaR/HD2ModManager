using System.Diagnostics;
using System.Text;

namespace HD2ModCore.Infrastructure;

// 作用：通过随管理器分发的 7-Zip 命令行运行时解压归档，隔离压缩格式解析并支持取消。
public sealed class SevenZipArchiveExtractor
{
	/// <param name="flatten">
	/// <c>true</c> = 只取文件、不重建包内的目录结构（<c>7z e</c>）。
	/// <para>
	/// <b>为什么要这个开关。</b>中文整合包的目录常常又深又长（实测一个 292 MB 的包里，
	/// 解压后最深路径到 <b>268</b> 字符），而临时目录本身还要占掉一百来个字符——
	/// 于是"解压到临时目录"这一步就会撞上 260 的路径上限。7-Zip 自己用 <c>\\?\</c> 能过，
	/// 但后续任何一个走老 API 的环节都会炸，而且报的是"找不到文件"这种南辕北辙的错。
	/// </para>
	/// <para>
	/// 摊平是安全的：VPK 转换只按<b>文件名</b>找包，包内的目录层次对它没有任何意义。
	/// 代价是包内同名文件会互相覆盖——所以调用方必须**先确认没有重名**再摊平
	/// （见 <c>VpkImportService.TryPrepareAsync</c>）。
	/// </para>
	/// </param>
	public async Task ExtractAsync(
		string archiveFilePath,
		string destinationDirectory,
		CancellationToken cancellationToken = default,
		bool flatten = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
		var archive = Path.GetFullPath(archiveFilePath);
		if (!File.Exists(archive)) throw new FileNotFoundException("Archive file not found.", archive);

		Directory.CreateDirectory(destinationDirectory);
		var (exitCode, output, error) = await RunAsync(
			[flatten ? "e" : "x", "-y", "-bd", $"-o{Path.GetFullPath(destinationDirectory)}", archive],
			cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
		{
			var detail = string.Join(Environment.NewLine, new[] { error, output }.Where(value => !string.IsNullOrWhiteSpace(value)));
			throw new InvalidDataException($"7-Zip extraction failed with exit code {exitCode}.{Environment.NewLine}{detail}".Trim());
		}
	}

	/// <summary>
	/// 列出归档里的条目路径（不解压）。
	/// <para>
	/// <b>为什么要先看一眼。</b>导入一个 357 MB 的角色包时，要不要走"VPK → patch"这条路
	/// 取决于包里到底有没有 <c>.vpk</c>。先解压再判断意味着普通 patch 包被解压两次，
	/// 而 <c>7z l</c> 只读目录区，几十毫秒就够。
	/// </para>
	/// <para>
	/// 只解析 <c>Path = …</c> 这一行，<b>不解析时间/大小列</b>——那些列在有空格的文件名下无法可靠切分。
	/// 条目名可能因为控制台代码页而乱码，但我们只用它判断扩展名，扩展名是 ASCII。
	/// </para>
	/// </summary>
	public async Task<IReadOnlyList<string>> ListAsync(string archiveFilePath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archiveFilePath);
		var archive = Path.GetFullPath(archiveFilePath);
		if (!File.Exists(archive)) throw new FileNotFoundException("Archive file not found.", archive);

		var (exitCode, output, error) = await RunAsync(["l", "-slt", "-ba", archive], cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
		{
			throw new InvalidDataException($"7-Zip listing failed with exit code {exitCode}.{Environment.NewLine}{error}".Trim());
		}

		var entries = new List<string>();
		foreach (var line in output.Split('\n'))
		{
			var trimmed = line.TrimEnd('\r');
			if (!trimmed.StartsWith("Path = ", StringComparison.Ordinal))
				continue;
			var path = trimmed["Path = ".Length..].Trim();
			if (path.Length != 0) entries.Add(path);
		}

		// 第一条永远是归档自身。
		if (entries.Count != 0) entries.RemoveAt(0);
		return entries;
	}

	private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		var executable = ResolveExecutablePath();
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				// 条目名里常有中文。控制台默认代码页会让它变成乱码，这里统一按 UTF-8 读。
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				WorkingDirectory = Path.GetDirectoryName(executable)!,
			}
		};
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

		if (!process.Start()) throw new InvalidOperationException("Unable to start bundled 7-Zip.");
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		using var registration = cancellationToken.Register(() =>
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
		});
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
	}

	private static string ResolveExecutablePath()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "third_party", "7zip", "7z.exe");
		if (!File.Exists(path)) throw new FileNotFoundException("Bundled 7-Zip executable is missing.", path);
		return path;
	}
}
