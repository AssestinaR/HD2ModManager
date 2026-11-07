using System.Text;

namespace HD2ModAdaptation.PatchReconstruction;

/// <summary>
/// 物理参数容器（<c>HD2PBX01</c> + EOF footer <c>HD2PBFT1</c>）在**重新构建 patch** 时的搬运工具。
/// <para>
/// 背景：`vpk2patch` 产出 → `custom-model convert`（应用为 RE2310）会**按条目重新写一个 patch**，
/// 尾部附加的数据不会自动跟过去。不显式搬一次，交付给游戏的那份就没有物理参数，
/// 运行时只能退回外部 rig 文件——这正是"换个模型就得手动换参数文件"的根源。
/// </para>
/// <para>
/// 这里只做纯字节搬运（不解析 rig 内容），所以不依赖 <c>HD2SourceModel</c>，
/// 让 <c>HD2ModCore</c>（GUI 直接调用的那层）也能用。
/// </para>
/// </summary>
public static class PhysBoneCarrier
{
	/// <summary>容器里的记录名（协议固定）。</summary>
	public const string RigRecordName = "hd2_spring_rig.bin";

	private static ReadOnlySpan<byte> ContainerMagic => "HD2PBX01"u8;
	private static ReadOnlySpan<byte> FooterMagic => "HD2PBFT1"u8;
	private const int FooterSize = 56;
	private const int HeaderSize = 48;

	/// <summary>从一份载体字节里取出完整容器（含 footer）；没有就返回 null。</summary>
	public static byte[]? ExtractContainer(ReadOnlySpan<byte> carrier)
	{
		if (carrier.Length < HeaderSize + FooterSize)
			return null;
		var footer = carrier[^FooterSize..];
		if (!footer[..8].SequenceEqual(FooterMagic))
			return null;
		var containerLength = BitConverter.ToUInt64(footer[16..24]);   // footer +0x10
		if (containerLength < HeaderSize + FooterSize || containerLength > (ulong)carrier.Length)
			return null;
		var start = carrier.Length - (int)containerLength;
		if (!carrier[start..].StartsWith(ContainerMagic))
			return null;
		return carrier[start..].ToArray();
	}

	/// <summary>把载体末尾已有的容器剥掉（没有就原样返回）。</summary>
	public static byte[] Strip(ReadOnlySpan<byte> carrier)
	{
		var container = ExtractContainer(carrier);
		return container is null ? carrier.ToArray() : carrier[..^container.Length].ToArray();
	}

	/// <summary>容器里 <see cref="RigRecordName"/> 的字节长度（拿不到返回 -1）。</summary>
	public static int ReadRigRecordLength(ReadOnlySpan<byte> carrierWithContainer)
	{
		var container = ExtractContainer(carrierWithContainer);
		if (container is null || container.Length < HeaderSize)
			return -1;
		var recordCount = BitConverter.ToUInt32(container, 0x28);
		var at = (int)BitConverter.ToUInt64(container, 0x18);
		for (var i = 0; i < recordCount; i++)
		{
			if (at + 56 > container.Length)
				return -1;
			var nameLength = BitConverter.ToUInt16(container, at);
			var dataLength = (int)BitConverter.ToUInt64(container, at + 12);
			var name = Encoding.UTF8.GetString(container, at + 56, nameLength);
			if (name == RigRecordName)
				return dataLength;
			at += 56 + nameLength;
		}
		return -1;
	}

	/// <summary>把容器追加到载体末尾（自动剥旧容器，幂等）。</summary>
	public static byte[] Append(ReadOnlySpan<byte> carrier, ReadOnlySpan<byte> container)
	{
		var clean = Strip(carrier);
		var result = new byte[clean.Length + container.Length];
		clean.CopyTo(result, 0);
		container.CopyTo(result.AsSpan(clean.Length));
		return result;
	}

	public sealed record CarryOutcome(bool Applied, bool ReplacedExisting, int Patches, long AddedBytes, int RigBytes, string? Note)
	{
		public static CarryOutcome Skip(string note) => new(false, false, 0, 0, -1, note);
	}

	/// <summary>
	/// 把 <paramref name="sourcePatchPath"/> 里的容器搬到 <paramref name="targetDirectory"/> 下
	/// 每个**含 Unit** 的 patch 上（材质 patch 跳过）。
	/// </summary>
	public static async Task<CarryOutcome> CarryForwardAsync(
		string sourcePatchPath,
		string targetDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

		var sourceGpu = sourcePatchPath + ".gpu_resources";
		if (!File.Exists(sourceGpu))
			return CarryOutcome.Skip("来源 patch 没有 .gpu_resources");
		var sourceBytes = await File.ReadAllBytesAsync(sourceGpu, cancellationToken).ConfigureAwait(false);
		var container = ExtractContainer(sourceBytes);
		if (container is null)
		{
			// ⚠️ 实测踩到过：`custom-model convert` 传给这一步的"来源 patch"可能是**临时裁剪件**
			// （PatchSubsetWriter 只搬被引用的条目，容器不在里面）。所以这里不能只认它—— 
			// 顺着 mod 目录（根 / vpk2patch / Overwrite）找一份真的带容器的，才搬得过去。
			container = await FindContainerNearAsync(sourcePatchPath, cancellationToken).ConfigureAwait(false);
			if (container is null)
				return CarryOutcome.Skip("来源与同目录都没有物理参数容器");
		}

		var scanner = new PatchTocScanner();
		var applied = 0;
		var replaced = false;
		long added = 0;
		var rigBytes = -1;
		var locked = new List<string>();

		foreach (var patchPath in EnumeratePatchFiles(targetDirectory))
		{
			var gpuPath = patchPath + ".gpu_resources";
			if (!File.Exists(gpuPath))
				continue;
			if (!await ContainsUnitAsync(scanner, patchPath, cancellationToken).ConfigureAwait(false))
				continue;

			byte[] carrier;
			try
			{
				carrier = await File.ReadAllBytesAsync(gpuPath, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				locked.Add(Path.GetFileName(patchPath) + "：" + exception.Message);
				continue;
			}

			var existing = ExtractContainer(carrier);
			replaced |= existing is not null;
			if (existing is not null && existing.AsSpan().SequenceEqual(container))
				continue;   // 已经一样了

			var combined = Append(carrier, container);
			try
			{
				await File.WriteAllBytesAsync(gpuPath, combined, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				locked.Add(Path.GetFileName(patchPath) + "：" + exception.Message);
				continue;
			}

			applied++;
			added += combined.Length - carrier.Length;
			rigBytes = ReadRigRecordLength(combined);
		}

		if (locked.Count > 0 && applied == 0)
			return CarryOutcome.Skip("文件被占用：" + string.Join("；", locked.Take(3)));
		return new CarryOutcome(
			applied > 0,
			replaced,
			applied,
			added,
			rigBytes,
			locked.Count > 0 ? "部分 patch 被占用未更新：" + string.Join("；", locked.Take(3)) : null);
	}

	/// <summary>顺着来源 patch 所在的 mod 目录找一份真的带容器的 patch（找不到返回 null）。</summary>
	private static async Task<byte[]?> FindContainerNearAsync(string sourcePatchPath, CancellationToken cancellationToken)
	{
		try
		{
			var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePatchPath));
			if (directory is null)
				return null;
			// 从"patch 所在目录"和"它的父目录（mod 根）"两处找：裁剪件在临时目录时父目录未必有用，
			// 但调用方传入的通常是 mod 内路径，两者覆盖足够。
			var roots = new List<string> { directory };
			var parent = Directory.GetParent(directory)?.FullName;
			if (parent is not null)
				roots.Add(parent);
			foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
			{
				foreach (var candidate in EnumeratePatchFiles(root))
				{
					var candidateGpu = candidate + ".gpu_resources";
					if (!File.Exists(candidateGpu))
						continue;
					var bytes = await File.ReadAllBytesAsync(candidateGpu, cancellationToken).ConfigureAwait(false);
					var found = ExtractContainer(bytes);
					if (found is not null)
						return found;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
		return null;
	}

	/// <summary>
	/// 从磁盘上的载体文件**只读尾部**取出容器（不把几十 MB 的 gpu 全读进内存）。
	/// </summary>
	public static async Task<byte[]?> TryReadContainerFromFileAsync(string path, CancellationToken cancellationToken = default)
	{
		if (!File.Exists(path))
			return null;
		try
		{
			await using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
			var size = handle.Length;
			if (size < HeaderSize + FooterSize)
				return null;
			var footer = new byte[FooterSize];
			handle.Position = size - FooterSize;
			var read = await handle.ReadAtLeastAsync(footer, FooterSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
			if (read < FooterSize || !footer.AsSpan(0, 8).SequenceEqual(FooterMagic))
				return null;
			var containerLength = BitConverter.ToUInt64(footer, 16);
			if (containerLength < HeaderSize + FooterSize || containerLength > (ulong)size)
				return null;
			var container = new byte[containerLength];
			handle.Position = size - (long)containerLength;
			read = await handle.ReadAtLeastAsync(container, container.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
			if (read < container.Length || !container.AsSpan(0, 8).SequenceEqual(ContainerMagic))
				return null;
			return container;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>把容器追加到载体文件末尾（先剥掉已有的同内容容器，幂等）。返回最终长度。</summary>
	public static async Task<long> AppendContainerToFileAsync(string path, byte[] container, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(container);
		await using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
		var size = handle.Length;

		// 已经带着**同一份**容器 ⇒ 不动（否则每次重建都多长 10 KB）
		if (size >= container.Length + HeaderSize)
		{
			var tail = new byte[container.Length];
			handle.Position = size - container.Length;
			var read = await handle.ReadAtLeastAsync(tail, tail.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
			if (read == tail.Length && tail.AsSpan().SequenceEqual(container))
				return size;
		}

		// 带着**旧**容器 ⇒ 先截掉，再追加新的
		var existing = await TryReadContainerFromFileAsync(path, cancellationToken).ConfigureAwait(false);
		if (existing is not null)
		{
			handle.SetLength(size - existing.Length);
			size -= existing.Length;
		}

		handle.Position = size;
		await handle.WriteAsync(container, cancellationToken).ConfigureAwait(false);
		return handle.Length;
	}

	/// <summary>
	/// 从一组候选载体里找第一份带容器的，把它追加到 <paramref name="targetGpuPath"/> 末尾。
	/// <para>
	/// 这是"尾附数据必须跟随"这条规则的**唯一实现**，供三个写入器共用：
	/// <c>PatchArchiveWriter</c>（补丁原语）、<c>CanonicalPatchWriter</c>（装配会话落地）、
	/// 以及跨护甲编排器（它在写入器之外再兜一次）。这样以后新增写入器只要调这一个方法，
	/// 就不会再出现"某个写入器悄悄把参数吃掉"的情况。
	/// </para>
	/// </summary>
	/// <returns>是否真的写入了（false = 候选里都没有容器，或目标已是最新）。</returns>
	public static async Task<bool> CarryFromAnyAsync(
		IReadOnlyList<string> candidateCarrierPaths,
		string targetGpuPath,
		CancellationToken cancellationToken = default)
	{
		if (candidateCarrierPaths is null || candidateCarrierPaths.Count == 0 || !File.Exists(targetGpuPath))
			return false;

		byte[]? container = null;
		foreach (var path in candidateCarrierPaths)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				continue;
			container = await TryReadContainerFromFileAsync(path, cancellationToken).ConfigureAwait(false);
			if (container is not null)
				break;
		}
		if (container is null)
			return false;

		var before = new FileInfo(targetGpuPath).Length;
		try
		{
			var after = await AppendContainerToFileAsync(targetGpuPath, container, cancellationToken).ConfigureAwait(false);
			return after != before;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 搬不过去不该让写 patch 失败，但也绝不静默：调用方会用返回值/日志体现。
			return false;
		}
	}

	/// <summary>
	/// **部署边界兜底**：在一个 mod 目录内部找一份带容器的 patch，把它补齐到所有含 Unit 的 patch。
	/// <para>
	/// 为什么需要：patch 会在多处被"按条目重写"（护甲标准化、装饰合并、头盔合并、应用为 RE2310…），
	/// 写入器已经会跟随，但只要有一条重写路径不经过它，容器就会静默消失，
	/// 运行时表现为"拿别的模型的参数去驱动"。这一道闸放在**部署前**，是最后的保证。
	/// </para>
	/// <returns>实际写入的 patch 数（0 表示本来就好、或者这个 mod 里根本没有容器）。</returns>
	/// </summary>
	public static async Task<int> EnsureAcrossModAsync(string modDirectory, CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(modDirectory))
			return 0;

		// 先在 mod 里找一份"现成的"容器（通常是 vpk2patch 那份，由导入时 embed-mod 写入）。
		byte[]? container = null;
		foreach (var patch in EnumeratePatchFiles(modDirectory))
		{
			container = await TryReadContainerFromFileAsync(patch + ".gpu_resources", cancellationToken).ConfigureAwait(false);
			if (container is not null)
				break;
		}
		if (container is null)
			return 0;

		var scanner = new PatchTocScanner();
		var applied = 0;
		foreach (var patch in EnumeratePatchFiles(modDirectory))
		{
			var gpu = patch + ".gpu_resources";
			if (!File.Exists(gpu))
				continue;
			if (!await ContainsUnitAsync(scanner, patch, cancellationToken).ConfigureAwait(false))
				continue;
			var existing = await TryReadContainerFromFileAsync(gpu, cancellationToken).ConfigureAwait(false);
			if (existing is not null && existing.AsSpan().SequenceEqual(container))
				continue;
			try
			{
				await AppendContainerToFileAsync(gpu, container, cancellationToken).ConfigureAwait(false);
				applied++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 单个文件写不进去（被游戏占用）不该让部署失败。
			}
		}
		return applied;
	}

	/// <summary>枚举一个 mod 目录里"可能是产物"的 patch：根 / vpk2patch / Overwrite。</summary>
	public static IEnumerable<string> EnumeratePatchFiles(string modDirectory)
	{
		foreach (var directory in new[]
		{
			modDirectory,
			Path.Combine(modDirectory, ModContentLayerNames.Sectional),
			Path.Combine(modDirectory, ModContentLayerNames.LegacySectional),
			Path.Combine(modDirectory, ModContentLayerNames.Overwrite),
		})
		{
			if (!Directory.Exists(directory))
				continue;
			foreach (var path in Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.TopDirectoryOnly))
			{
				if (path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
					|| path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
					continue;
				yield return path;
			}
		}
	}

	private static async Task<bool> ContainsUnitAsync(
		PatchTocScanner scanner,
		string patchPath,
		CancellationToken cancellationToken)
	{
		try
		{
			var entries = await scanner.ScanEntriesAsync(patchPath, cancellationToken).ConfigureAwait(false);
			return entries.Any(entry => entry.AssetKey.TypeId == UnitMesh.PatchUnitMeshReader.UnitTypeId);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or IndexOutOfRangeException)
		{
			return false;
		}
	}
}
