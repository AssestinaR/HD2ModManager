namespace HD2ModManager.Views;

// Pure projection planning for ModListTransitionController. This type has no
// dependency on WPF containers or animation state, which keeps the expensive
// visual controller focused on lifecycle and rendering only.
internal static class ModListProjectionPlanner
{
    internal sealed record Entry(string Key, object Item);
    internal sealed record MoveOperation(int FromIndex, int ToIndex);
    internal sealed record ReorderPlan(
        IReadOnlyList<MoveOperation> Operations,
        string Strategy,
        int ForwardMoveCount,
        int BackwardMoveCount);

    public static List<Entry> CreateEntries(
        IReadOnlyList<object> desired,
        Func<object?, string?> keySelector,
        Action<string>? onDuplicateKey = null,
        Action<object?>? onMissingKey = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keySelector);

        var result = new List<Entry>(desired.Count);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in desired)
        {
            var key = keySelector(item);
            if (string.IsNullOrWhiteSpace(key))
            {
                onMissingKey?.Invoke(item);
                continue;
            }
            if (!keys.Add(key))
            {
                onDuplicateKey?.Invoke(key);
                continue;
            }
            result.Add(new Entry(key, item));
        }
        return result;
    }

    public static ReorderPlan PlanDirectionalMoves(
        IReadOnlyList<string> current,
        IReadOnlyList<string> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var forward = BuildDirectionalMovePlan(current, desired, fromEnd: false);
        var backward = BuildDirectionalMovePlan(current, desired, fromEnd: true);
        var useBackward = backward.Count < forward.Count;
        return new ReorderPlan(
            useBackward ? backward : forward,
            useBackward ? "backward" : "forward",
            forward.Count,
            backward.Count);
    }

    private static IReadOnlyList<MoveOperation> BuildDirectionalMovePlan(
        IReadOnlyList<string> current,
        IReadOnlyList<string> desired,
        bool fromEnd)
    {
        var working = current.ToList();
        var result = new List<MoveOperation>();
        if (!fromEnd)
        {
            for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                if (string.Equals(working[targetIndex], desired[targetIndex], StringComparison.OrdinalIgnoreCase)) continue;
                var existingIndex = FindKeyIndex(working, desired[targetIndex], targetIndex + 1, working.Count - 1);
                if (existingIndex < 0) return Array.Empty<MoveOperation>();
                Move(working, existingIndex, targetIndex);
                result.Add(new MoveOperation(existingIndex, targetIndex));
            }
            return result;
        }

        for (var targetIndex = desired.Count - 1; targetIndex >= 0; targetIndex--)
        {
            if (string.Equals(working[targetIndex], desired[targetIndex], StringComparison.OrdinalIgnoreCase)) continue;
            var existingIndex = FindKeyIndex(working, desired[targetIndex], 0, targetIndex - 1);
            if (existingIndex < 0) return Array.Empty<MoveOperation>();
            Move(working, existingIndex, targetIndex);
            result.Add(new MoveOperation(existingIndex, targetIndex));
        }
        return result;
    }

    private static void Move(List<string> items, int fromIndex, int toIndex)
    {
        var value = items[fromIndex];
        items.RemoveAt(fromIndex);
        items.Insert(toIndex, value);
    }

    private static int FindKeyIndex(IReadOnlyList<string> items, string key, int minimumIndex, int maximumIndex)
    {
        for (var index = Math.Max(0, minimumIndex); index <= Math.Min(maximumIndex, items.Count - 1); index++)
        {
            if (string.Equals(items[index], key, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }
}
