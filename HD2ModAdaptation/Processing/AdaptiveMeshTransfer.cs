using HD2ModAdaptation.PatchReconstruction.UnitMesh;

// 文件作用：这是“自适应网格转移”的流程协调器。
//
// 它负责把以下步骤串联起来：
// 1. 判断源 patch 是材质内嵌模式还是材质分离模式；
// 2. 必要时，在内存中临时合并模型 patch 和材质 patch 的目录条目；
// 3. 从合并后的数据中读取源 Unit；
// 4. 检查源 mesh 和目标 mesh 的索引是否合法；
// 5. 调用 MeshTransfer 执行实际的网格转换；
// 6. 返回转换后的目标模型以及本次流程的状态信息。
//
// 本文件本身不负责实现底层算法，具体工作由其他组件完成：
// - PatchMaterialDetector：检测 patch 的材质组织模式；
// - TemporaryMaterialCombiner：临时组合模型和材质 patch 的目录条目；
// - MeshTransfer：转换顶点、网格 section、骨骼索引和材质绑定；
// - AdaptiveOutputWriter：将转换结果写成输出文件（不在本文件中调用）。
namespace HD2ModAdaptation.Processing;

[Obsolete("已过时：替换护甲生产统一使用 DirectSourceUnitReuse；保留用于兼容旧调用。")]
public sealed class AdaptiveMeshTransfer
{
	// 材质模式检测器：负责判断源 patch 是内嵌材质还是分离材质。
	private readonly PatchMaterialDetector detector;
	// 临时合并器：负责把模型 patch 和可选的材质 patch 在内存中组合起来。
	private readonly TemporaryMaterialCombiner combiner;
	// 网格转换器：负责真正修改目标模型中的网格数据。
	private readonly MeshTransfer meshTransfer;

	// 可以从外部传入组件实例，主要用于测试或替换具体实现。
	// 如果调用方没有传入，就使用这里创建的默认组件。
	public AdaptiveMeshTransfer(
		PatchMaterialDetector? detector = null,
		TemporaryMaterialCombiner? combiner = null,
		MeshTransfer? meshTransfer = null)
	{
		this.detector = detector ?? new PatchMaterialDetector();
		this.combiner = combiner ?? new TemporaryMaterialCombiner();
		this.meshTransfer = meshTransfer ?? new MeshTransfer();
	}

	/// <summary>
	/// 将源 patch 中指定的网格转移到目标模型中，并兼容材质内嵌和材质分离两种 patch 形式。
	/// </summary>
	/// <param name="sourcePatchPath">源模型 patch 文件的路径。</param>
	/// <param name="targetModel">要接收网格的目标 Unit 模型。</param>
	/// <param name="targetMeshIndex">目标模型中要被替换的 mesh 索引。</param>
	/// <param name="sourceMeshIndex">源模型中要使用的 mesh 索引，默认使用第 0 个。</param>
	/// <param name="materialPatchPaths">可选的材质 patch 路径列表，主要用于材质分离的源 patch。</param>
	public async Task<AdaptiveMeshTransferResult> TransferAsync(
		string sourcePatchPath,
		UnitMeshModel targetModel,
		int targetMeshIndex,
		int sourceMeshIndex = 0,
		IReadOnlyList<string>? materialPatchPaths = null)
	{
		ArgumentNullException.ThrowIfNull(sourcePatchPath);
		ArgumentNullException.ThrowIfNull(targetModel);

		// 第一步：检测源 patch 的材质组织模式。
		// 这里只是读取目录条目并进行判断，不会修改源 patch 文件。
		var detection = await detector.DetectAsync(sourcePatchPath);
		Console.WriteLine($"Detected: {detection.GetDescription()}");

		// 第二步：如果检测出源 patch 是材质分离模式，但调用方没有提供材质 patch，
		// 仍然继续执行，但提前警告：后续读取到的材质引用可能不完整。
		if (detection.Mode == PatchMaterialMode.Separated && 
		    (materialPatchPaths == null || materialPatchPaths.Count == 0))
		{
			Console.WriteLine("⚠️ Warning: Separated patch detected but no materials provided");
			Console.WriteLine("   Material references may be incomplete");
		}

		// 第三步：准备读取源 Unit 所需的目录条目。
		// 如果提供了材质 patch，combiner 会把它们与模型 patch 的条目临时组合；
		// 这里不会修改原始模型 patch 或材质 patch 文件。
		var combined = await combiner.CombineAsync(sourcePatchPath, materialPatchPaths);
		Console.WriteLine($"Combined: {combined.GetDescription()}");

		// 第四步：从准备好的条目集合中读取第 0 个 Unit。
		// 注意：当前流程固定读取第 0 个 Unit，并不是根据目标 mesh 自动寻找 Unit。
		var sourceUnit = await combiner.ReadCombinedUnitAsync(combined, 0);
		var sourceModel = sourceUnit.Model;

		// 第五步：检查源 mesh 索引是否存在。
		// 这是为了避免后续转换时访问不存在的 RawMeshData。
		if (sourceMeshIndex < 0 || sourceMeshIndex >= sourceModel.RawMeshData.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(sourceMeshIndex),
				$"Source mesh index {sourceMeshIndex} is out of range (0-{sourceModel.RawMeshData.Count - 1})");
		}

		// 检查目标 mesh 索引是否存在。
		// 目标模型由调用方传入，本文件不会在这里自动选择目标 mesh。
		if (targetMeshIndex < 0 || targetMeshIndex >= targetModel.RawMeshData.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(targetMeshIndex),
				$"Target mesh index {targetMeshIndex} is out of range (0-{targetModel.RawMeshData.Count - 1})");
		}

		// 第六步：调用真正执行网格转换的 MeshTransfer。
		// 这里将源模型指定 mesh 的数据转移到目标模型指定 mesh 中；
		// 顶点转换、材质槽映射和骨骼索引重映射都在 MeshTransfer 内部完成。
		var transferResult = meshTransfer.Transfer(
			targetModel,
			targetMeshIndex,
			sourceModel,
			sourceMeshIndex);

		return new AdaptiveMeshTransferResult(
			transferResult.Model,
			detection.Mode,
			combined.WasCombined,
			combined.MaterialPatchPaths,
			sourceMeshIndex,
			targetMeshIndex);
	}
}

/// <summary>
/// 自适应网格转移的结果。
///
/// 这个结果包含两类信息：
/// 1. 转换后的模型；
/// 2. 本次流程是如何处理源 patch 的记录。
///
/// 它只表示网格转移流程返回了结果，不能单独证明材质依赖完整、
/// 输出文件一定兼容当前游戏版本，或输出文件一定能在游戏中正常显示。
/// </summary>
public sealed record AdaptiveMeshTransferResult(
	UnitMeshModel UpdatedModel,
	PatchMaterialMode OriginalMode,
	bool WasCombined,
	IReadOnlyList<string> UsedMaterialPaths,
	int SourceMeshIndex,
	int TargetMeshIndex)
{
	// 当前定义表示：源 patch 原本是内嵌模式，或者流程确实合并过材质 patch。
	// 它不等同于“材质依赖已完整验证”或“最终输出已经通过游戏验证”。
	public bool IsComplete => OriginalMode == PatchMaterialMode.Embedded || WasCombined;
	
	// 根据源 patch 的原始材质模式，生成面向人的简短流程摘要。
	public string GetSummary() => OriginalMode switch
	{
		PatchMaterialMode.Embedded => $"Embedded patch processed (no combining needed)",
		PatchMaterialMode.Separated when WasCombined => 
			$"Separated patch combined with {UsedMaterialPaths.Count} material patches",
		PatchMaterialMode.Separated => 
			$"Separated patch processed without materials (may be incomplete)",
		_ => "Unknown"
	};
}
