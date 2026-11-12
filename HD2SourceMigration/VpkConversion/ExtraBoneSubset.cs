using HD2SourceMigration.Retarget;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 一个 unit 该带哪些物理（jiggle）骨的选择结果。
/// </summary>
/// <param name="Extras">该 unit 真正要带的那部分合成骨（父链已闭包、父下标已按新表重映射）。</param>
/// <param name="Dropped">没带进来的合成骨根数（<c>原数 - Extras.Count</c>）。0 = 与全量一致。</param>
/// <param name="Warning">
/// 非 null 表示<b>没有切分</b>（退回全量）。只在容器结构与"合成骨起始下标"的假设不成立时出现，
/// 调用方应当把它报出来 —— 静默退回全量会让"每件只带自己的骨"这条优化悄悄失效。
/// </param>
public sealed record ExtraBoneSelection(
	IReadOnlyList<RetargetExtraBone> Extras,
	int Dropped,
	string? Warning)
{
	/// <summary>这次真的切掉了骨（否则 <see cref="Extras"/> 与输入等价）。</summary>
	public bool Subsetted => Warning is null && Dropped > 0;
}

/// <summary>
/// 把一个 unit 的<b>物理骨清单裁到它自己用得到的那几根</b>。
/// <para>
/// <b>为什么必须裁。</b>转换器把整套合成骨并进了**每个** unit 的 TransformInfo（分段模型的每个 unit
/// 都带一份 <c>source_extra_*</c>），而运行时是"逐 unit 逐链驱动"的：7 个 unit × 39 条链 = 273 链/玩家，
/// 其中绝大部分骨上**根本没有几何**（实测 Lunalice：1 份容器 57 骨 / 39 链，
/// 而 7 个 unit 各自只渲染自己那几块布料）。裁完之后跑一次游戏日志应该变成
/// <c>已挂载 7 个 unit / 十几~几十条链</c>，而不是 <c>273</c>。
/// </para>
/// <para>
/// <b>判据是蒙皮数据，不是材质名。</b>顶点影响里带的是骨名哈希（<see cref="RetargetedInfluence.BoneHash"/>），
/// 所以"这个 unit 用到哪些骨"在转换期是**已知的精确答案**，不需要按材质关键词猜。
/// 猜错的代价是"某块布料不动了"，而那是实机才能发现的事。
/// </para>
/// <para>
/// <b>为什么还要往上闭包。</b>合成骨是**链**（父也常常是合成骨）。只带叶子而不带它的祖先，
/// <see cref="UnitTransformInfoAppender"/> 会拿到越界的父下标；即使不越界，链也会断在中间。
/// 所以选中的骨必须沿 <see cref="RetargetExtraBone.ParentIndex"/> 一路带上祖先，直到落在原生骨上为止。
/// </para>
/// <para>
/// ⚠️ <b>不要为了"每根骨只出现一次"而跨 unit 去重。</b>同一个 unit 内部不能有重复骨，但两个 unit
/// 各自需要同一根骨是**正常**的（裙摆上下两段都挂在 hips 上）。目标是"每个 unit 只带自己用到的"。
/// </para>
/// </summary>
public static class ExtraBoneSubset
{
	/// <summary>从一份已建好的网格里推出该 unit 要带的骨。</summary>
	/// <param name="extras">全量合成骨（<see cref="RetargetResult.ExtraBones"/>）。</param>
	/// <param name="baseCount">合成骨在 TransformInfo 里的起始下标（= 原生骨根数）。</param>
	/// <param name="mesh">这一个 unit 的几何（顶点影响已按骨名哈希写好）。</param>
	public static ExtraBoneSelection Select(
		IReadOnlyList<RetargetExtraBone>? extras,
		int baseCount,
		RetargetedMesh? mesh)
	{
		if (extras is null || extras.Count == 0)
			return new ExtraBoneSelection(extras ?? [], 0, null);
		if (mesh is null)
			return new ExtraBoneSelection(extras, 0, null);

		var used = new HashSet<uint>();
		foreach (var vertex in mesh.Vertices)
		{
			foreach (var influence in vertex.Influences)
				used.Add(influence.BoneHash);
		}

		return Select(extras, baseCount, used);
	}

	/// <summary>从一组骨名哈希推出该 unit 要带的骨（测试与"已经算好哈希"的调用方用）。</summary>
	public static ExtraBoneSelection Select(
		IReadOnlyList<RetargetExtraBone> extras,
		int baseCount,
		IReadOnlyCollection<uint> usedHashes)
	{
		ArgumentNullException.ThrowIfNull(extras);
		ArgumentNullException.ThrowIfNull(usedHashes);
		if (extras.Count == 0)
			return new ExtraBoneSelection(extras, 0, null);

		// 结构自检：合成骨的父要么是原生骨（下标 < baseCount），要么是在它**之前**的合成骨。
		// 这条不成立就说明 baseCount 传错了（或者骨表不是"原生 + 追加"的形状），
		// 那时候任何切分都可能把父链弄断 ⇒ 直接退回全量，并让调用方把话说明白。
		if (baseCount < 0)
			return Full(extras, $"合成骨起始下标 {baseCount} 非法，未做切分");
		for (var index = 0; index < extras.Count; index++)
		{
			var parent = extras[index].ParentIndex;
			if (parent < baseCount)
				continue;
			if (parent - baseCount >= index)
			{
				return Full(extras,
					$"合成骨 {extras[index].Name} 的父下标 {parent} 不在它之前（起始 {baseCount}），未做切分");
			}
		}

		var indexByHash = new Dictionary<uint, int>(extras.Count);
		for (var index = 0; index < extras.Count; index++)
			indexByHash.TryAdd(extras[index].NameHash, index);

		var keep = new bool[extras.Count];
		foreach (var hash in usedHashes)
		{
			if (indexByHash.TryGetValue(hash, out var index))
				keep[index] = true;
		}

		// 祖先闭包。倒序扫：父下标恒小于子下标（上面刚验过），所以从后往前走时
		// "打开一个父"一定发生在它自己被扫到之前，一趟就够。
		for (var index = extras.Count - 1; index >= 0; index--)
		{
			if (!keep[index])
				continue;
			var parent = extras[index].ParentIndex;
			while (parent >= baseCount)
			{
				var parentIndex = parent - baseCount;
				if (keep[parentIndex])
					break;
				keep[parentIndex] = true;
				parent = extras[parentIndex].ParentIndex;
			}
		}

		var picked = new List<int>();
		for (var index = 0; index < extras.Count; index++)
		{
			if (keep[index])
				picked.Add(index);
		}

		if (picked.Count == extras.Count)
			return new ExtraBoneSelection(extras, 0, null);

		// 重排父下标：保留的骨按原顺序排（父仍在子之前），新下标 = baseCount + 位置。
		var positionByIndex = new Dictionary<int, int>(picked.Count);
		for (var position = 0; position < picked.Count; position++)
			positionByIndex[picked[position]] = baseCount + position;

		var result = new List<RetargetExtraBone>(picked.Count);
		foreach (var index in picked)
		{
			var extra = extras[index];
			var parent = extra.ParentIndex;
			if (parent >= baseCount)
			{
				if (!positionByIndex.TryGetValue(parent - baseCount, out var remapped) || remapped >= baseCount + result.Count)
				{
					// 闭包跑过之后不可能发生；真发生就是这层逻辑错了（宁可炸在转换期，也不要写出一份断链的骨架）。
					throw new InvalidOperationException(
						$"合成骨 {extra.Name} 的父（下标 {parent}）没有跟着闭包进来，父链会断。");
				}

				parent = remapped;
			}

			result.Add(extra with { ParentIndex = parent });
		}

		return new ExtraBoneSelection(result, extras.Count - result.Count, null);
	}

	private static ExtraBoneSelection Full(IReadOnlyList<RetargetExtraBone> extras, string warning)
		=> new(extras, 0, warning);
}
