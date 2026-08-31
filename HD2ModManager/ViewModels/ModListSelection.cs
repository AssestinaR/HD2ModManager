using System.Windows.Input;

namespace HD2ModManager.ViewModels;

public enum ModListSelectionPolicy
{
    None,
    Single,
    Multiple,
    ReadOnly,
}

// Every item presented by an animated list needs an identity independent from
// whether the host allows selecting it. Plan cards, for example, are keyed but
// are not themselves selectable rows.
public interface IModListKeyed
{
    string ListKey { get; }
}

// The shared row contract deliberately keeps presentation data separate from
// Mod/domain state.  A list host decides what "enabled" means in its own
// context; the shared panel only renders the resulting concise facts.
public enum ModListRowTone
{
    Muted,
    Accent,
    Success,
    Warning,
    Danger,
}

public sealed record ModListRowPresentation(
    string? TypeLabel = null,
    ModListRowTone TypeTone = ModListRowTone.Muted,
    string? ActivationLabel = null,
    ModListRowTone ActivationTone = ModListRowTone.Muted,
    string? WarningLabel = null,
    ModListRowTone WarningTone = ModListRowTone.Warning,
    string? Summary = null,
    string? OrderLabel = null,
    ModListRowTone OrderTone = ModListRowTone.Accent,
    string? Note = null)
{
    public static ModListRowPresentation Empty { get; } = new();

    public bool HasType => !string.IsNullOrWhiteSpace(TypeLabel);
    public bool HasActivation => !string.IsNullOrWhiteSpace(ActivationLabel);
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningLabel);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasOrder => !string.IsNullOrWhiteSpace(OrderLabel);
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

public interface IModListSelectable : IModListKeyed
{
    string SelectionKey { get; }
    bool IsSelected { get; set; }
    bool IsDecoration { get; }
    ModListRowPresentation RowPresentation { get; }
}

// Internal drag is meaningful only for rows whose host owns a real ordering
// dimension.  Decorations may share the same visual list as options, but they
// deliberately do not opt into option-order drag operations.
public interface IModListInternalReorderable
{
    bool CanInternalReorder { get; }
}

public sealed class ModListSelectionRequestEventArgs(object item, IReadOnlyList<string> selectedKeys, ModifierKeys modifiers) : EventArgs
{
    public object Item { get; } = item;
    public IReadOnlyList<string> SelectedKeys { get; } = selectedKeys;
    public ModifierKeys Modifiers { get; } = modifiers;
}
