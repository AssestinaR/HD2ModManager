using System.Windows.Input;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Views;

// Separate bottom-bar source for actions on the current selection. Keeping it
// distinct from the temporary editor lets both occupy independently animated rows.
public sealed class SelectionActionBarPresentation : BaseViewModel
{
    private readonly BottomBarCoordinator _bottomBar;

    public SelectionActionBarPresentation(
        BottomBarCoordinator bottomBar,
        ICommand selectionPrimaryCommand,
        ICommand selectionDeleteCommand,
        ICommand deleteFromLibraryCommand,
        ICommand enableDecorationsCommand,
        ICommand disableDecorationsCommand,
        ICommand setSelectedModsAsOptionsCommand,
        ICommand attachSelectedDecorationsCommand,
        ICommand removeSelectedOptionsFromHostCommand,
        ICommand promoteArmorProvidersCommand,
        ICommand registerArmorTargetsCommand,
        ICommand releaseArmorProvidersCommand,
        ICommand releaseArmorTargetsCommand,
        ICommand clearArmorTargetsCommand,
        ICommand cancelSelectionCommand)
    {
        _bottomBar = bottomBar;
        SelectionPrimaryCommand = selectionPrimaryCommand;
        SelectionDeleteCommand = selectionDeleteCommand;
        DeleteFromLibraryCommand = deleteFromLibraryCommand;
        EnableDecorationsCommand = enableDecorationsCommand;
        DisableDecorationsCommand = disableDecorationsCommand;
        SetSelectedModsAsOptionsCommand = setSelectedModsAsOptionsCommand;
        AttachSelectedDecorationsCommand = attachSelectedDecorationsCommand;
        RemoveSelectedOptionsFromHostCommand = removeSelectedOptionsFromHostCommand;
        PromoteArmorProvidersCommand = promoteArmorProvidersCommand;
        RegisterArmorTargetsCommand = registerArmorTargetsCommand;
        ReleaseArmorProvidersCommand = releaseArmorProvidersCommand;
        ReleaseArmorTargetsCommand = releaseArmorTargetsCommand;
        ClearArmorTargetsCommand = clearArmorTargetsCommand;
        CancelSelectionCommand = cancelSelectionCommand;
        BeginMoveCommand = bottomBar.BeginMoveCommand;
        BeginInsertCommand = bottomBar.BeginInsertCommand;
        bottomBar.PropertyChanged += (_, eventArgs) => OnPropertyChanged(eventArgs.PropertyName);
    }

    public string SelectionSummary => _bottomBar.SelectionSummary;
    public bool ShowAddToProfile => _bottomBar.ShowAddToProfile;
    public bool ShowMove => _bottomBar.ShowMove;
    public string MoveButtonText => _bottomBar.MoveButtonText;
    public bool ShowInsert => _bottomBar.ShowInsert;
    public bool ShowDelete => _bottomBar.ShowDelete;
    public bool ShowRemove => _bottomBar.ShowRemove;
    public bool ShowDeleteFromLibrary => _bottomBar.ShowDeleteFromLibrary;
    public bool ShowEnableDecorations => _bottomBar.ShowEnableDecorations;
    public bool ShowDisableDecorations => _bottomBar.ShowDisableDecorations;
    public bool ShowSetAsOption => _bottomBar.ShowSetAsOption;
    public string SetAsOptionText => _bottomBar.SetAsOptionText;
    public bool ShowAttachDecorationsToHost => _bottomBar.ShowAttachDecorationsToHost;
    public string AttachDecorationsToHostText => _bottomBar.AttachDecorationsToHostText;
    public bool ShowRemoveOptionsFromHost => _bottomBar.ShowRemoveOptionsFromHost;
    public bool ShowPromoteArmorProviders => _bottomBar.ShowPromoteArmorProviders;
    public bool ShowRegisterArmorTargets => _bottomBar.ShowRegisterArmorTargets;
    public bool ShowReleaseArmorProviders => _bottomBar.ShowReleaseArmorProviders;
    public bool ShowReleaseArmorTargets => _bottomBar.ShowReleaseArmorTargets;
    public bool ShowClearArmorTargets => _bottomBar.ShowClearArmorTargets;
    public ICommand BeginMoveCommand { get; }
    public ICommand BeginInsertCommand { get; }
    public ICommand SelectionPrimaryCommand { get; }
    public ICommand SelectionDeleteCommand { get; }
    public ICommand DeleteFromLibraryCommand { get; }
    public ICommand EnableDecorationsCommand { get; }
    public ICommand DisableDecorationsCommand { get; }
    public ICommand SetSelectedModsAsOptionsCommand { get; }
    public ICommand AttachSelectedDecorationsCommand { get; }
    public ICommand RemoveSelectedOptionsFromHostCommand { get; }
    public ICommand PromoteArmorProvidersCommand { get; }
    public ICommand RegisterArmorTargetsCommand { get; }
    public ICommand ReleaseArmorProvidersCommand { get; }
    public ICommand ReleaseArmorTargetsCommand { get; }
    public ICommand ClearArmorTargetsCommand { get; }
    public ICommand CancelSelectionCommand { get; }
}
