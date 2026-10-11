using HD2SourceModel;

namespace HD2SourceMigration;

/// <summary>HD2 骨架里的一个变换节点。</summary>
/// <param name="Index">在 TransformInfo 表里的下标；父子关系也用这个下标引用。</param>
/// <param name="Name">节点名（如 <c>l_shoulder</c>）。读不到名字时为 <c>null</c>。</param>
/// <param name="ParentIndex">父节点下标。根节点指向自己或越界值。</param>
/// <param name="WorldBind">
/// 世界 bind 变换，**已统一成列向量约定**（平移在第 4 列）。
/// HD2 文件里是行向量存储，转换发生在 <see cref="Hd2RigReader.FromHd2RowVector"/>。
/// </param>
public sealed record Hd2RigBone(int Index, string? Name, int ParentIndex, Mat4 WorldBind)
{
	public bool HasName => !string.IsNullOrEmpty(Name);

	/// <summary>没有可读名字时的占位名，与源侧的 <c>source_extra_N</c> 同构。</summary>
	public string DisplayName => HasName ? Name! : $"hd2_extra_{Index}";

	/// <summary>该节点在世界空间里的位置。</summary>
	public Vec3 WorldOrigin => WorldBind.Origin;
}

/// <summary>
/// HD2 avatar 骨架：世界 bind 变换 + 父子关系 + 名字。
/// <para>
/// 数据全部来自<b>游戏本体</b>——变换表来自 avatar Unit 的 TocData，名字来自游戏的 Bones 资源。
/// 刻意不依赖任何外部参考表：外部表一旦与游戏版本脱节，映射就会静默错位。
/// </para>
/// </summary>
public sealed record Hd2Rig(IReadOnlyList<Hd2RigBone> Bones)
{
	public static Hd2Rig Empty { get; } = new(Array.Empty<Hd2RigBone>());

	private IReadOnlyDictionary<string, Hd2RigBone>? byName;

	/// <summary>按名字查节点（区分大小写）。名字重复时取下标最小的那个，保证结果稳定。</summary>
	public Hd2RigBone? Find(string name)
	{
		ArgumentNullException.ThrowIfNull(name);
		byName ??= Bones
			.Where(bone => bone.HasName)
			.GroupBy(bone => bone.Name!, StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => group.OrderBy(bone => bone.Index).First(),
				StringComparer.Ordinal);

		return byName.TryGetValue(name, out var found) ? found : null;
	}
}
