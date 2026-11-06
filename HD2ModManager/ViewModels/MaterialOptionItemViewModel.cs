using System;
using System.Windows.Input;
using HD2ModCore.Infrastructure;
using HD2ModManager.Controls.Cards;

namespace HD2ModManager.ViewModels;

/// <summary>
/// 材质列表里的一行。
/// <para>
/// <b>它不是库里的 Mod。</b>一个材质选项就是主 Mod 里的一个 patch：所以这里**没有删除、
/// 没有移出列表**——只要 patch 还在基底目录里，它就永远在这张表里。能改的只有
/// "启不启用、排第几、叫什么、备注是什么、配哪张图"。
/// </para>
/// </summary>
public sealed class MaterialOptionItemViewModel : BaseViewModel
{
    private readonly Action _changed;
    private string? _name;
    private string? _notes;
    private int _order;
    private bool _enabled;
    private string? _imageFileName;

    public MaterialOptionItemViewModel(MaterialOptionView option, Action changed)
    {
        _changed = changed;
        PatchIndex = option.PatchIndex;
        PatchFileName = option.PatchFileName;
        _name = option.Name;
        _notes = option.Notes;
        _order = option.Order;
        _enabled = option.Enabled;
        _imageFileName = option.ImageFileName;
        ToggleCommand = new RelayCommand(() => Enabled = !Enabled);
        MoveUpCommand = new RelayCommand(() => MoveRequested?.Invoke(this, -1));
        MoveDownCommand = new RelayCommand(() => MoveRequested?.Invoke(this, +1));
        ChooseImageCommand = new RelayCommand(() => ImageRequested?.Invoke(this, EventArgs.Empty));
        ClearImageCommand = new RelayCommand(() =>
        {
            ImageFileName = null;
            Persist(() => MaterialOptionSettings.SetImage(ModDirectory!, PatchIndex, null));
        });
        OpenFolderCommand = new RelayCommand(() => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>所属 Mod 的目录；由页面在构建时填入。</summary>
    public string? ModDirectory { get; set; }

    public int PatchIndex { get; }
    public string PatchFileName { get; }

    public event EventHandler<int>? MoveRequested;
    public event EventHandler? ImageRequested;
    public event EventHandler? OpenFolderRequested;

    public ICommand ToggleCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand ClearImageCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(_name) ? $"材质 {PatchIndex}" : _name!;

    public string? Name
    {
        get => _name;
        set
        {
            if (!SetField(ref _name, value)) return;
            OnPropertyChanged(nameof(DisplayName));
            Persist(() => MaterialOptionSettings.SetName(ModDirectory!, PatchIndex, value));
        }
    }

    public string? Notes
    {
        get => _notes;
        set
        {
            if (!SetField(ref _notes, value)) return;
            Persist(() => MaterialOptionSettings.SetNotes(ModDirectory!, PatchIndex, value));
        }
    }

    public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);

    public int Order
    {
        get => _order;
        set => SetField(ref _order, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetField(ref _enabled, value)) return;
            _changed();
        }
    }

    public string? ImageFileName
    {
        get => _imageFileName;
        set
        {
            if (!SetField(ref _imageFileName, value)) return;
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ImagePath));
        }
    }

    public bool HasImage => !string.IsNullOrWhiteSpace(_imageFileName);

    public string? ImagePath => HasImage && ModDirectory is not null
        ? System.IO.Path.Combine(MaterialOptionSettings.GetOptionsDirectory(ModDirectory), _imageFileName!)
        : null;

    private void Persist(Func<string?> write)
    {
        _ = write();
        _changed();
    }
}
