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
        ICommand cancelSelectionCommand)
    {
        _bottomBar = bottomBar;
        SelectionPrimaryCommand = selectionPrimaryCommand;
        SelectionDeleteCommand = selectionDeleteCommand;
        DeleteFromLibraryCommand = deleteFromLibraryCommand;
        EnableDecorationsCommand = enableDecorationsCommand;
        DisableDecorationsCommand = disableDecorationsCommand;
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
    public ICommand BeginMoveCommand { get; }
    public ICommand BeginInsertCommand { get; }
    public ICommand SelectionPrimaryCommand { get; }
    public ICommand SelectionDeleteCommand { get; }
    public ICommand DeleteFromLibraryCommand { get; }
    public ICommand EnableDecorationsCommand { get; }
    public ICommand DisableDecorationsCommand { get; }
    public ICommand CancelSelectionCommand { get; }
}
