using System.Buffers.Binary;
using System.Diagnostics;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 用社区工具链自带的 <c>texconv.exe</c> 把任意 DDS 编成 BC7。
/// 移植自 <c>tmp/rig-lab/hd2_texpack.py:77-93</c>。
/// <para>
/// <b>为什么走外部进程而不是自己写 BC7 编码器</b>：Python 侧测过的那套配方
/// （<c>-f BC7_UNORM[_SRGB] -m 1</c>）与社区工具链字节一致；自己实现编码器是另一个量级的工作，
/// 而且任何细微差别都会静默改变贴图观感。BC7 本身是压缩格式，同样的输入在不同编码器下
/// 输出不同但都"合法"，所以**没有便宜的验证手段**。
/// </para>
/// <para>
/// <b>⚠️ 不要重定向标准输出。</b>本仓库的 DSH 沙箱不允许程序通过管道拿另一个程序的输出
/// （Node 的 <c>stdio:'pipe'</c> 会 EPERM），所以这里一律 <c>RedirectStandardOutput = false</c>、
/// 用 <c>CreateNoWindow</c> 压住窗口，靠<b>产物文件</b>判断成功而不是靠 stdout。
/// </para>
/// <para>
/// 临时文件是必须的：texconv 只吃文件不吃标准输入。
/// </para>
/// </summary>
public sealed class Bc7Encoder
{
	/// <summary>社区工具链里的相对路径。仓库里两个位置字节相同，随便哪个都行。</summary>
	private static readonly string[] RelativeCandidates =
	[
		Path.Combine("tools", "ref", "HD2SDK-CommunityEdition", "deps", "texconv.exe"),
		Path.Combine("docs", "HD2SDK-CommunityEdition-3.8.0", "deps", "texconv.exe"),
	];

	public Bc7Encoder(string texConvPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(texConvPath);
		if (!File.Exists(texConvPath))
			throw new FileNotFoundException($"找不到 texconv.exe：{texConvPath}", texConvPath);
		TexConvPath = Path.GetFullPath(texConvPath);
	}

	public string TexConvPath { get; }

	/// <summary>
	/// 从 <paramref name="startDirectory"/> 逐级向上找 <c>texconv.exe</c>；
	/// 显式给了 <paramref name="explicitPath"/> 且存在则优先用它。找不到返回 <c>null</c>
	/// —— 调用方应当据此跳过，而不是假装编码成功。
	/// </summary>
	public static string? Locate(string? explicitPath = null, string? startDirectory = null)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
			return Path.GetFullPath(explicitPath);

		var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
		while (directory is not null)
		{
			foreach (var relative in RelativeCandidates)
			{
				var candidate = Path.Combine(directory.FullName, relative);
				if (File.Exists(candidate))
					return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}

	/// <summary>
	/// 把一份 DDS（任意 texconv 认得的格式）编成 BC7，返回**完整的输出 DDS 字节**
	/// （148 字节 DX10 头 + 像素），与 <c>hd2_texpack.py</c> 的产物形状一致。
	/// </summary>
	/// <param name="srgb">底色用 <c>BC7_UNORM_SRGB</c>(99)；法线/粗糙/金属/掩码用 <c>BC7_UNORM</c>(98)。</param>
	/// <param name="mipCount">要保留的 mip 层数；HD2 出货只留 <c>mip0</c>，所以默认 1。</param>
	public byte[] EncodeToBc7(byte[] sourceDds, bool srgb, int mipCount = 1)
	{
		var format = srgb ? "BC7_UNORM_SRGB" : "BC7_UNORM";
		var output = Transcode(sourceDds, format, mipCount);
		ValidateBc7Output(output, srgb);
		return output;
	}

	/// <summary>
	/// 通用转码：任意 texconv 认得的 DDS → 指定格式。返回**完整的输出 DDS 字节**。
	/// <para>
	/// 之所以需要它而不只有 <see cref="EncodeToBc7"/>：底色贴图必须先解成未压缩 RGBA
	/// 才能把 alpha 清零，再编成 BC7 —— 两步。BC7 是压缩格式，直接改它的 alpha 要自己实现编码器。
	/// </para>
	/// </summary>
	public byte[] Transcode(byte[] sourceDds, string formatName, int mipCount = 1)
	{
		ArgumentNullException.ThrowIfNull(sourceDds);
		ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
		if (sourceDds.Length < 128)
			throw new InvalidDataException($"输入 DDS 只有 {sourceDds.Length} 字节，连头都不够。");

		var workDirectory = Path.Combine(Path.GetTempPath(), "hd2-bc7-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workDirectory);
		try
		{
			var inputPath = Path.Combine(workDirectory, "input.dds");
			File.WriteAllBytes(inputPath, sourceDds);

			var startInfo = new ProcessStartInfo(TexConvPath)
			{
				WorkingDirectory = workDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				// ⚠️ 绝不重定向：沙箱下管道 stdio 会 EPERM（见类型注释）。
				RedirectStandardOutput = false,
				RedirectStandardError = false,
				RedirectStandardInput = false,
			};
			foreach (var argument in new[] { "-nologo", "-y", "-f", formatName, "-m", mipCount.ToString(), "-o", workDirectory, inputPath })
				startInfo.ArgumentList.Add(argument);

			using (var process = Process.Start(startInfo)
				?? throw new InvalidOperationException($"启动 texconv 失败：{TexConvPath}"))
			{
				process.WaitForExit();
				// 靠产物判断成功，不靠 stdout（我们刻意没重定向它）。
				if (!File.Exists(inputPath))
					throw new InvalidDataException($"texconv 退出码 {process.ExitCode}，且没有产出文件。");
			}

			return File.ReadAllBytes(inputPath);
		}
		finally
		{
			try
			{
				if (Directory.Exists(workDirectory))
					Directory.Delete(workDirectory, recursive: true);
			}
			catch (IOException)
			{
				// 清理失败不能盖掉编码结果。
			}
		}
	}

	/// <summary>
	/// 解成未压缩 <c>R8G8B8A8</c> 并**剥掉 DDS 头**，返回裸像素（每像素 4 字节）。
	/// <para>
	/// ⚠️ <b>剥头是刻意的。</b>曾经这里返回的是完整 DDS，于是调用方各自按需要处理：
	/// 有的（`ZeroAlpha`）跳过头，有的（`BuildOpacityMask`、`TextureAlpha.Measure`、
	/// `BakeMatCap`、`AlbedoAdjustment`）把它当裸像素——**后者全部是错的**，
	/// 其中 `BuildOpacityMask` 会产出头与数据不符的 DDS、让 texconv 原样退回输入，
	/// 表现为"某材质转换失败，产物只有 132 字节"。
	/// </para>
	/// <para>
	/// 返回裸像素之后，"要不要包头"就只由调用方显式决定（需要 DDS 时用
	/// <c>Hd2TextureAsset.BuildDds</c> 包一层），没有隐式约定可以踩错。
	/// </para>
	/// </summary>
	public byte[] DecodeToRgba(byte[] sourceDds, int mipCount = 1)
	{
		var dds = Transcode(sourceDds, "R8G8B8A8_UNORM", mipCount);
		const int headerSize = 128;
		if (dds.Length < headerSize)
			throw new InvalidDataException($"texconv 解出来的 DDS 只有 {dds.Length} 字节，剥不掉头。");
		return dds[headerSize..];
	}

	/// <summary>
	/// 产物必须是带 DX10 扩展头的 BC7。texconv 不认输入时会<b>原样复制</b>并把退出码置 0，
	/// 所以只校验"文件存在"是不够的——那会让未编码的输入静默混进包里。
	/// </summary>
	private static void ValidateBc7Output(byte[] dds, bool srgb)
	{
		if (dds.Length < Hd2TextureAsset.DdsHeaderSize)
			throw new InvalidDataException($"texconv 产物只有 {dds.Length} 字节，不足一个 DDS 头。");
		if (!dds.AsSpan(84, 4).SequenceEqual("DX10"u8))
			throw new InvalidDataException("texconv 产物不是 DX10 DDS —— 输入格式它可能不认识，原样复制了。");

		var expected = srgb ? Hd2TextureAsset.Bc7UnormSrgb : Hd2TextureAsset.Bc7Unorm;
		var actual = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128));
		if (actual != expected)
			throw new InvalidDataException($"texconv 产物 DXGI 格式是 {actual}，期望 {expected}。");
	}
}
