using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// The library stores Mods as independent directories, while option ownership is
// expressed by each Option's HostModGuids.  This graph is the single place that
// turns those flat relations back into a safe recursive tree for UI, defaults,
// deletion, and deployment.
public sealed class OptionRelationshipGraph
{
    private static readonly IReadOnlyList<ModNode> EmptyNodes = Array.Empty<ModNode>();

    private readonly IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNode>> _childrenByParent;
    private readonly IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNodeId>> _parentsByChild;

    private OptionRelationshipGraph(
        IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNode>> childrenByParent,
        IReadOnlyDictionary<ModNodeId, IReadOnlyList<ModNodeId>> parentsByChild)
    {
        _childrenByParent = childrenByParent;
        _parentsByChild = parentsByChild;
    }

    public static OptionRelationshipGraph Create(LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var children = new Dictionary<ModNodeId, List<ModNode>>();
        var parents = new Dictionary<ModNodeId, HashSet<ModNodeId>>();

        foreach (var option in snapshot.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Option))
        {
            foreach (var rawParentId in option.Metadata.HostModGuids ?? Array.Empty<string>())
            {
                if (!Guid.TryParse(rawParentId, out var parsedParentId)) continue;
                var parentId = new ModNodeId(parsedParentId);
                if (!snapshot.Nodes.TryGetValue(parentId, out var parent)) continue;
                if (parent.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option)) continue;

                if (!children.TryGetValue(parentId, out var direct))
                    children[parentId] = direct = new List<ModNode>();
                if (direct.Any(existing => existing.Id == option.Id)) continue;
                direct.Add(option);

                if (!parents.TryGetValue(option.Id, out var directParents))
                    parents[option.Id] = directParents = new HashSet<ModNodeId>();
                directParents.Add(parentId);
            }
        }

        var orderedChildren = children.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ModNode>)pair.Value
                .OrderBy(node => node.Metadata.OptionOrder ?? int.MaxValue)
                .ThenBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Id.Value)
                .ToArray());
        var orderedParents = parents.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ModNodeId>)pair.Value.OrderBy(id => id.Value).ToArray());
        return new OptionRelationshipGraph(orderedChildren, orderedParents);
    }

    public IReadOnlyList<ModNode> GetDirectOptions(ModNodeId parentId)
        => _childrenByParent.TryGetValue(parentId, out var children) ? children : EmptyNodes;

    public IReadOnlyList<ModNodeId> GetParents(ModNodeId optionId)
        => _parentsByChild.TryGetValue(optionId, out var parents) ? parents : Array.Empty<ModNodeId>();

    public IReadOnlyList<ModNode> GetDescendantOptions(ModNodeId parentId)
    {
        var result = new List<ModNode>();
        var emitted = new HashSet<ModNodeId>();
        var ancestors = new HashSet<ModNodeId> { parentId };
        Visit(parentId, ancestors, result, emitted);
        return result;
    }

    private void Visit(
        ModNodeId parentId,
        HashSet<ModNodeId> ancestors,
        List<ModNode> result,
        HashSet<ModNodeId> emitted)
    {
        foreach (var child in GetDirectOptions(parentId))
        {
            // Do not turn malformed legacy metadata into an infinite traversal.
            if (!ancestors.Add(child.Id)) continue;
            if (emitted.Add(child.Id)) result.Add(child);
            Visit(child.Id, ancestors, result, emitted);
            ancestors.Remove(child.Id);
        }
    }
}
