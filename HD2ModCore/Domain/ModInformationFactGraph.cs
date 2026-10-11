namespace HD2ModCore.Domain;

// 作用：把登记表里的静态依赖边物化成一张有向图，并提供拓扑序与传递闭包。
// Purpose: Materializes the static dependency edges of the registry into a directed graph with a
//          topological order and transitive closure.
//
// 边的方向约定：A → B 表示"生产 A 需要先拿到 B"。因此 TopologicalOrder 里 B 一定排在 A 前面，
// 即"按拓扑序依次生产"不会出现"用到还没算出来的上游"。
// Edge direction: A → B means "producing A requires B first", so B precedes A in TopologicalOrder.
public sealed class ModInformationFactGraph
{
	private readonly IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> _direct;
	private readonly IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> _transitive;

	private ModInformationFactGraph(
		IReadOnlyList<ModInformationPropertyKind> nodes,
		IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> direct,
		IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> transitive,
		IReadOnlyList<ModInformationPropertyKind> topologicalOrder,
		bool isAcyclic)
	{
		Nodes = nodes;
		_direct = direct;
		_transitive = transitive;
		TopologicalOrder = topologicalOrder;
		IsAcyclic = isAcyclic;
	}

	// 作用：图中全部节点，按枚举值稳定排序。
	// Purpose: All nodes, ordered stably by enum value.
	public IReadOnlyList<ModInformationPropertyKind> Nodes { get; }

	// 作用：依赖优先的拓扑序；有环时退化为"能排的排完、剩下的按枚举序补齐"。
	// Purpose: Dependency-first topological order; when a cycle exists it degrades to a partial order.
	public IReadOnlyList<ModInformationPropertyKind> TopologicalOrder { get; }

	public bool IsAcyclic { get; }

	// 作用：直接上游（登记表里逐条声明、逐条核对过的那 21 条边）。
	// Purpose: Direct upstream kinds (the reviewed edges declared in the registry).
	public IReadOnlyList<ModInformationPropertyKind> DirectDependencies(ModInformationPropertyKind kind)
		=> _direct.TryGetValue(kind, out var value) ? value : Array.Empty<ModInformationPropertyKind>();

	// 作用：传递闭包——"生产本 kind 之前必须先就绪的全部上游"。
	// Purpose: Transitive closure — every upstream kind that must be ready before producing this one.
	public IReadOnlyList<ModInformationPropertyKind> TransitiveDependencies(ModInformationPropertyKind kind)
		=> _transitive.TryGetValue(kind, out var value) ? value : Array.Empty<ModInformationPropertyKind>();

	// 作用：从登记表构建依赖图。
	// Purpose: Builds the graph from the registry descriptors.
	public static ModInformationFactGraph Build(
		IReadOnlyDictionary<ModInformationPropertyKind, FactDescriptor> descriptors)
	{
		ArgumentNullException.ThrowIfNull(descriptors);
		var nodes = descriptors.Keys.OrderBy(kind => (int)kind).ToArray();
		var direct = new Dictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>>(nodes.Length);
		foreach (var node in nodes)
		{
			direct[node] = descriptors[node].Dependencies
				.Where(dependency => descriptors.ContainsKey(dependency))
				.Distinct()
				.OrderBy(kind => (int)kind)
				.ToArray();
		}

		var (order, isAcyclic) = SortTopologically(nodes, direct);
		var transitive = new Dictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>>(nodes.Length);
		foreach (var node in nodes)
			transitive[node] = ComputeClosure(node, direct, nodes);

		return new ModInformationFactGraph(nodes, direct, transitive, order, isAcyclic);
	}

	// 作用：深度优先拓扑排序；发现回边即判定有环（并把有环节点也放进结果，避免"静默少节点"）。
	// Purpose: DFS topological sort; a back edge marks a cycle (cycled nodes are still emitted, never silently dropped).
	private static (IReadOnlyList<ModInformationPropertyKind> Order, bool IsAcyclic) SortTopologically(
		IReadOnlyList<ModInformationPropertyKind> nodes,
		IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> direct)
	{
		var order = new List<ModInformationPropertyKind>(nodes.Count);
		var state = new Dictionary<ModInformationPropertyKind, int>(nodes.Count); // 0 未访问 / 1 访问中 / 2 已完成
		var isAcyclic = true;

		void Visit(ModInformationPropertyKind node)
		{
			var current = state.GetValueOrDefault(node, 0);
			if (current == 2) return;
			if (current == 1)
			{
				isAcyclic = false;
				return;
			}

			state[node] = 1;
			foreach (var dependency in direct.TryGetValue(node, out var value) ? value : Array.Empty<ModInformationPropertyKind>())
				Visit(dependency);
			state[node] = 2;
			order.Add(node);
		}

		foreach (var node in nodes)
			Visit(node);

		// Visit 先递归上游再登记本节点，因此 order 天然是"依赖在前"。
		return (order, isAcyclic);
	}

	private static IReadOnlyList<ModInformationPropertyKind> ComputeClosure(
		ModInformationPropertyKind root,
		IReadOnlyDictionary<ModInformationPropertyKind, IReadOnlyList<ModInformationPropertyKind>> direct,
		IReadOnlyList<ModInformationPropertyKind> nodes)
	{
		var visited = new HashSet<ModInformationPropertyKind>();
		var pending = new Stack<ModInformationPropertyKind>();
		foreach (var dependency in direct.TryGetValue(root, out var value) ? value : Array.Empty<ModInformationPropertyKind>())
			pending.Push(dependency);

		while (pending.Count > 0)
		{
			var current = pending.Pop();
			if (!visited.Add(current)) continue;
			foreach (var dependency in direct.TryGetValue(current, out var value) ? value : Array.Empty<ModInformationPropertyKind>())
				pending.Push(dependency);
		}

		return nodes.Where(visited.Contains).ToArray();
	}
}
