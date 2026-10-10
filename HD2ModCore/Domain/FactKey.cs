namespace HD2ModCore.Domain;

// 作用：区分事实主体的种类，让"某个东西的某个方面"在全仓只有一个名字。
// Purpose: Distinguishes the kind of fact subject so one aspect of one thing has a single repository-wide name.
public enum FactSubjectKind
{
	// 库里的一个 Mod 节点（目录节点）。
	Node,
	// 一个游戏资产（typeId + fileId）。
	GameAsset,
	// 全局游戏数据，与具体 Mod 无关。
	GameData,
	// 库级状态或库级聚合视图。
	Library,
}

// 作用：事实的主体。用结构化值而不是字符串拼接，避免分隔符冲突（旧键用 "|" 拼接，节点名含 "|" 就会歧义）。
// Purpose: The subject of a fact. Structured rather than concatenated, avoiding delimiter collisions.
public readonly record struct FactSubject(FactSubjectKind Kind, string Value)
{
	public static FactSubject Node(ModNodeId id) => new(FactSubjectKind.Node, id.Value.ToString("N"));

	public static FactSubject GameAsset(ulong typeId, ulong fileId)
		=> new(FactSubjectKind.GameAsset, $"{typeId:x16}:{fileId:x16}");

	public static FactSubject GameData() => new(FactSubjectKind.GameData, "global");

	public static FactSubject Library() => new(FactSubjectKind.Library, "global");

	// 作用：库快照事实的主体——仍是库，但快照覆盖的节点集合是身份的一部分，用于按节点精确失效。
	// Purpose: Subject of a library-snapshot fact: still the library, but the covered node set is part of the identity.
	public static FactSubject LibrarySnapshot(IEnumerable<ModNodeId> nodeIds)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		var token = string.Join(',', nodeIds.OrderBy(id => id.Value).Select(id => id.Value.ToString("N")));
		return new FactSubject(FactSubjectKind.Library, token);
	}

	// 作用：判断本主体是否为该 Mod 节点；供按节点失效时做结构化匹配，替代旧的字符串前缀匹配。
	// Purpose: Structured node match used by per-node invalidation instead of string prefix matching.
	public bool IsNode(ModNodeId id)
		=> Kind == FactSubjectKind.Node
			&& string.Equals(Value, id.Value.ToString("N"), StringComparison.Ordinal);

	public override string ToString() => $"{Kind}:{Value}";
}

// 作用：信息中心内所有事实的唯一标识。一个 FactKey 精确描述"哪一个实体的哪一个方面"。
// Purpose: The single identity of every fact: which subject, which aspect.
public readonly record struct FactKey(FactSubject Subject, ModInformationPropertyKind Kind)
{
	// 作用：为某个 Mod 节点的某个属性构造身份；节点缺失时用空 GUID 占位，与旧键行为一致。
	// Purpose: Builds the identity of one property of one node; a missing node falls back to Guid.Empty like the old key.
	public static FactKey ForNode(ModNodeId? nodeId, ModInformationPropertyKind kind)
		=> new(FactSubject.Node(nodeId ?? new ModNodeId(Guid.Empty)), kind);

	public override string ToString() => $"{Subject}/{Kind}";
}

// 作用：事实的完整缓存身份 = 事实身份 + 输入指纹。对应设计文档"所有缓存键收敛为 (FactKey, RevisionKey)"。
// Purpose: The full cache identity of a fact: its FactKey plus its input fingerprint.
public readonly record struct FactRevisionKey(FactKey Key, string Revision)
{
	public override string ToString() => $"{Key}@{Revision}";
}
