using System.Windows.Input;

namespace HD2ModManager.ViewModels;

public enum ModListSelectionPolicy
{
    None,
    Single,
    Multiple,
    ReadOnly,
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
    string? Summary = null)
{
    public static ModListRowPresentation Empty { get; } = new();

    public bool HasType => !string.IsNullOrWhiteSpace(TypeLabel);
    public bool HasActivation => !string.IsNullOrWhiteSpace(ActivationLabel);
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningLabel);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
}

public interface IModListSelectable
{
    string SelectionKey { get; }
    bool IsSelected { get; set; }
    bool IsDecoration { get; }
    ModListRowPresentation RowPresentation { get; }
}

public sealed class ModListSelectionRequestEventArgs(object item, IReadOnlyList<string> selectedKeys, ModifierKeys modifiers) : EventArgs
{
    public object Item { get; } = item;
    public IReadOnlyList<string> SelectedKeys { get; } = selectedKeys;
    public ModifierKeys Modifiers { get; } = modifiers;
}
