using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

// 文件作用：负责把 AdaptiveMeshTransfer 产生的更新后模型写出为 patch 文件。
//
// 这个文件位于“网格转换”之后、“最终输出文件”阶段，主要流程是：
// 1. 接收已经完成网格转移的 AdaptiveMeshTransferResult；
// 2. 根据源 patch 原本的材质模式，选择内嵌模式或分离模式输出；
// 3. 调用 UnitMeshWriter 把 UnitMeshModel 序列化成 TOC 和 GPU 数据；
// 4. 将这些数据写入 output.patch_0、output.patch_0.gpu_resources 和 output.patch_0.stream。
//
// 本文件不负责：
// - 判断 patch 是否真的包含完整材质；
// - 执行网格、顶点、骨骼或材质槽转换；
// - 从原始 patch 中提取材质和纹理依赖；
// - 验证输出 patch 是否能被当前游戏接受。
//
// 重要限制：当前“分离模式”主要是输出策略上的区分。代码会写出模型及材质引用，
// 但没有在这里重新扫描并严格过滤所有 Material、Texture 或其他依赖条目。
namespace HD2ModAdaptation.Processing;

[Obsolete("已过时：替换护甲生产统一使用 DirectSourceUnitReuse；保留用于兼容旧调用。")]
public sealed class AdaptiveOutputWriter
{
	// UnitMeshWriter：负责把内存中的 UnitMeshModel 转换为可写入文件的二进制数据。
	private readonly UnitMeshWriter writer;

	// 允许外部传入 writer，便于测试或替换写出实现；未传入时使用默认 writer。
	public AdaptiveOutputWriter(UnitMeshWriter? writer = null)
	{
		this.writer = writer ?? new UnitMeshWriter();
	}

	/// <summary>
	/// 根据源 patch 的原始材质模式，写出更新后的模型。
	///
	/// 内嵌模式：写出模型及当前模型对象中包含的材质信息。
	///
	/// 分离模式：写出模型数据和材质引用，并返回需要继续复用的材质 patch 路径。
	/// 当前实现不会在这里重新构建完整的材质依赖闭包。
	/// </summary>
	public async Task<OutputResult> WriteAsync(
		AdaptiveMeshTransferResult transferResult,
		string outputDirectory)
	{
		ArgumentNullException.ThrowIfNull(transferResult);
		ArgumentNullException.ThrowIfNull(outputDirectory);

		Directory.CreateDirectory(outputDirectory);

		if (transferResult.OriginalMode == PatchMaterialMode.Embedded)
		{
			// 源 patch 被判断为内嵌模式，因此按照内嵌模式写出。
			return await WriteEmbeddedAsync(transferResult.UpdatedModel, outputDirectory);
		}
		else
		{
			// 除 Embedded 之外的情况都按照分离模式处理，
			// 包括 Separated 和当前无法确定材质模式的 Unknown。
			return await WriteSeparatedAsync(transferResult, outputDirectory);
		}
	}

	/// <summary>
	/// 按内嵌模式写出 patch。
	///
	/// 这里把更新后的模型交给 UnitMeshWriter，并将生成的 TOC 数据和 GPU 数据分别写出。
	/// </summary>
	private async Task<OutputResult> WriteEmbeddedAsync(
		UnitMeshModel model,
		string outputDirectory)
	{
		Console.WriteLine("Writing as embedded patch (model + materials)...");

		// 将模型交给写出器。
		// Empty<byte>() 表示这里没有额外传入独立的 GPU 数据；具体模型数据由 writer 根据
		// UnitMeshModel 自身的内容重新生成。
		var result = writer.Write(model, Array.Empty<byte>());

		// output.patch_0 保存目录表/TOC 数据。
		var outputPath = Path.Combine(outputDirectory, "output.patch_0");
		await File.WriteAllBytesAsync(outputPath, result.TocData);
		// .gpu_resources 保存网格等 GPU 资源数据。
		await File.WriteAllBytesAsync(outputPath + ".gpu_resources", result.GpuData);
		// 当前实现创建空的 .stream 文件作为配套文件。
		await File.WriteAllBytesAsync(outputPath + ".stream", Array.Empty<byte>());

		Console.WriteLine($"Written: {outputPath}");
		Console.WriteLine($"  - TOC: {result.TocData.Length} bytes");
		Console.WriteLine($"  - GPU: {result.GpuData.Length} bytes");

		return new OutputResult(
			OutputMode.Embedded,
			[outputPath],
			[],
			$"Embedded patch written: TOC={result.TocData.Length} bytes, GPU={result.GpuData.Length} bytes");
	}

	/// <summary>
	/// 按分离模式写出 patch。
	///
	/// 这里输出的是模型数据以及模型中的材质引用；实际材质数据应继续由单独的材质 patch 提供。
	///
	/// 当前实现仍然调用完整的 UnitMeshWriter 写出模型，并没有在 TOC 层面重新扫描、筛选和重建
	/// Material/Texture 条目。因此，“分离模式”不能理解为已经完成严格的材质依赖过滤。
	/// </summary>
	private async Task<OutputResult> WriteSeparatedAsync(
		AdaptiveMeshTransferResult transferResult,
		string outputDirectory)
	{
		Console.WriteLine("Writing as separated patch (model only)...");
		Console.WriteLine("⚠️ 注意：输出包含模型数据和材质引用");
		Console.WriteLine("   实际材质数据应继续来自单独的材质 patch");

		// 写出更新后的模型。
		// 这里保留模型中的材质引用，但没有把独立材质 patch 的原始文件内容复制进来。
		var result = writer.Write(transferResult.UpdatedModel, Array.Empty<byte>());

		// 与内嵌模式一样，分别写出 TOC、GPU 资源和当前为空的 stream 文件。
		var outputPath = Path.Combine(outputDirectory, "output.patch_0");
		await File.WriteAllBytesAsync(outputPath, result.TocData);
		await File.WriteAllBytesAsync(outputPath + ".gpu_resources", result.GpuData);
		await File.WriteAllBytesAsync(outputPath + ".stream", Array.Empty<byte>());

		Console.WriteLine($"Written: {outputPath}");
		Console.WriteLine($"  - TOC: {result.TocData.Length} bytes");
		Console.WriteLine($"  - GPU: {result.GpuData.Length} bytes");

		// 记录调用方在转换阶段实际提供并成功找到的材质 patch。
		// 这些路径只是说明和输出结果的一部分；本方法不会复制或修改这些文件。
		var notes = new List<string>
		{
			$"模型 patch 已写出：TOC={result.TocData.Length} 字节，GPU={result.GpuData.Length} 字节",
			"⚠️ 重要：请继续使用原始材质 patch："
		};

		foreach (var matPath in transferResult.UsedMaterialPaths)
		{
			notes.Add($"  - {Path.GetFileName(matPath)}");
		}

		if (transferResult.UsedMaterialPaths.Count == 0)
		{
			notes.Add("  （转换过程中没有提供材质 patch）");
		}

		return new OutputResult(
			OutputMode.Separated,
			[outputPath],
			transferResult.UsedMaterialPaths,
			string.Join(Environment.NewLine, notes));
	}
}

public enum OutputMode
{
	Embedded,   // 模型和材质放在同一个 patch 中。
	Separated   // 模型单独输出，材质由其他 patch 提供。
}

/// <summary>
/// 输出操作的结果。
///
/// 这里记录生成了哪些模型 patch、需要复用哪些材质 patch，以及面向人的说明文字。
/// </summary>
public sealed record OutputResult(
	OutputMode Mode,
	IReadOnlyList<string> ModelPatchPaths,
	IReadOnlyList<string> MaterialPatchPaths,
	string Notes)
{
	// 在控制台打印本次输出的模式、文件数量、材质 patch 和附加说明。
	public void PrintSummary()
	{
		Console.WriteLine($"\n=== 输出摘要 ===");
		Console.WriteLine($"模式：{Mode}");
		Console.WriteLine($"模型 Patch 数量：{ModelPatchPaths.Count}");
		foreach (var path in ModelPatchPaths)
		{
			Console.WriteLine($"  - {Path.GetFileName(path)}");
		}
		
		if (MaterialPatchPaths.Count > 0)
		{
			Console.WriteLine($"材质 Patch（复用）数量：{MaterialPatchPaths.Count}");
			foreach (var path in MaterialPatchPaths)
			{
				Console.WriteLine($"  - {Path.GetFileName(path)}");
			}
		}
		
		Console.WriteLine($"\n说明：\n{Notes}");
	}
}
