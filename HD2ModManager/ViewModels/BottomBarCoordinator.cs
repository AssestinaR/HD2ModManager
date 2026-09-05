using System;
using System.Collections.Generic;
using System.Linq;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels
{
    // 作用：集中派生多选操作与临时编辑控件，供窗口底部上下文操作栏统一呈现。
    public sealed class BottomBarCoordinator : BaseViewModel
    {
        private readonly SelectionCoordinator _selection;
        private readonly ModLibraryService _library;
        private readonly ProfileService _profiles;
        private readonly NotificationService _notifications;
        private readonly Action _refresh;
        private bool _isLibraryProfileCompanionVisible;
        private string? _relationshipHostId;
        private string? _relationshipHostName;
        private string? _editModId;
        private string? _editMode;
        private string? _editHostId;
        private string _editText = string.Empty;
        private string? _selectedProfile;
        private bool _suppressSelectionCancel;

        public BottomBarCoordinator(SelectionCoordinator selection, ModLibraryService library, ProfileService profiles, NotificationService notifications, Action refresh)
        {
            _selection = selection;
            _library = library;
            _profiles = profiles;
            _notifications = notifications;
            _refresh = refresh;
            ConfirmEditCommand = new RelayCommand(async _ => await ConfirmEditAsync());
            CancelEditCommand = new RelayCommand(CancelEdit);
            BeginMoveCommand = new RelayCommand(_ => BeginMove());
            BeginInsertCommand = new RelayCommand(_ => BeginInsert());
            Registrations.LayoutChanged += (_, snapshot) => LayoutChanged?.Invoke(this, snapshot);
            _selection.SelectionChanged += (_, _) =>
            {
                if (!_suppressSelectionCancel) CancelEdit();
                if (_suppressSelectionCancel) return;
                RefreshState();
            };
        }

        public RelayCommand ConfirmEditCommand { get; }
        public RelayCommand CancelEditCommand { get; }
        public RelayCommand BeginMoveCommand { get; }
        public RelayCommand BeginInsertCommand { get; }
        public event EventHandler? StructureChanged;
        public event EventHandler<BottomBarLayoutSnapshot>? LayoutChanged;
        public BottomBarRegistrationStore Registrations { get; } = new();
        public BottomBarLayoutSnapshot Layout => Registrations.Snapshot;
        public BottomBarRegistrationToken RegisterSurfaceSource(BottomBarRegistrationRequest request)
            => Registrations.Register(request);

        public void UpdateSurfaceSource(BottomBarRegistrationRequest request)
            => Registrations.Upsert(request);

        public void RemoveSurfaceSource(string sourceId)
            => Registrations.Remove(sourceId);
        public bool HasSelection => _selection.HasSelection;
        public bool HasTemporaryEditor => !string.IsNullOrWhiteSpace(_editMode);
        public bool IsPositionEditor => _editMode is "Move" or "Insert" or "OptionMove";
        public bool HasContent => HasSelection || HasTemporaryEditor;
        public bool IsLibrarySelection => HasSelection && string.Equals(_selection.Scope, "Library", StringComparison.OrdinalIgnoreCase);
        public bool IsProfileSelection => HasSelection && string.Equals(_selection.Scope, "Profile", StringComparison.OrdinalIgnoreCase);
        public bool IsDecorationHostSelection => HasSelection && _selection.Scope?.StartsWith("DecorationHost:", StringComparison.Ordinal) == true;
        public bool IsRelationshipSourceSelection => IsLibrarySelection || IsProfileSelection;
        public string? RelationshipHostId => _relationshipHostId;
        public string RelationshipHostName => _relationshipHostName ?? "当前 Mod";
        private bool HasSelectedDecorations => _selection.SelectedIds.Any(id => _library.Get(id)?.IsDecoration == true);
        private bool HasSelectedOptions => _selection.SelectedIds.Any(id => _library.Get(id)?.IsOption == true);
        private bool HasSelectedStandardMods => _selection.SelectedIds.Any(id => _library.Get(id)?.Capabilities.CanJoinProfile == true);
        private bool HasRelationshipHost => GetRelationshipHost() is not null;
        private bool HasSelectedOptionCandidates => GetSelectedOptionCandidateIds().Count != 0;
        private bool HasSelectedAttachableDecorations => !string.IsNullOrWhiteSpace(_relationshipHostId)
            && _selection.SelectedIds.Any(id => _library.Get(id)?.IsDecoration == true
                && _library.IsDecorationAvailableForHost(id, _relationshipHostId));
        private bool HasSelectedDirectHostOptions => TryGetSelectedDirectHostOptions(out _, out _);
        public bool ShowAddToProfile => IsLibrarySelection && _isLibraryProfileCompanionVisible && HasSelectedStandardMods;
        public bool ShowSetAsOption => IsRelationshipSourceSelection && HasRelationshipHost && HasSelectedOptionCandidates;
        public bool ShowAttachDecorationsToHost => IsRelationshipSourceSelection && HasRelationshipHost && HasSelectedAttachableDecorations;
        public bool ShowEnableDecorations => (IsLibrarySelection || IsDecorationHostSelection) && (HasSelectedDecorations || HasSelectedOptions);
        public bool ShowDisableDecorations => (IsLibrarySelection || IsDecorationHostSelection) && (HasSelectedDecorations || HasSelectedOptions);
        public bool ShowDelete => IsLibrarySelection;
        public bool ShowRemove => IsProfileSelection;
        public bool ShowRemoveOptionsFromHost => IsDecorationHostSelection && HasSelectedDirectHostOptions;
        public bool ShowDeleteFromLibrary => IsProfileSelection;
        public bool ShowMove => IsProfileSelection || TryGetSelectedDirectHostOptions(out _, out _);
        public bool ShowInsert => IsLibrarySelection && _isLibraryProfileCompanionVisible && HasSelectedStandardMods;
        public string MoveButtonText => IsDecorationHostSelection ? "调整顺序" : "移动";
        public string SetAsOptionText => $"设为“{RelationshipHostName}”的选项";
        public string AttachDecorationsToHostText => $"附加到“{RelationshipHostName}”";
        public string SelectionSummary => _selection.Summary;
        public string EditLabel => IsPositionEditor ? string.Empty
            : _editMode == "CreateProfile" ? "新建配置："
            : _editMode == "CreateLogicalMod" ? "新建逻辑 Mod："
            : _editMode == "SwitchProfile" ? "选择配置："
            : _editMode == "RenameProfile" ? "重命名配置："
            : string.Equals(_editMode, "Description", StringComparison.Ordinal) ? "修改备注：" : "修改名称：";
        public string PositionLabel => _editMode == "Move" ? "移动到配置顺序："
            : _editMode == "Insert" ? "插入到配置顺序："
            : _editMode == "OptionMove" ? "调整到选项顺序："
            : string.Empty;
        public string PositionHint => IsPositionEditor
            ? $"允许范围：1..{Math.Max(1, GetPositionMaximum())}"
            : string.Empty;
        public string EditConfirmText => "确定";
        public bool IsProfileSwitchEditor => _editMode == "SwitchProfile";
        public bool IsTextEditor => HasTemporaryEditor && !IsProfileSwitchEditor;
        public IEnumerable<string> ProfileOptions => _profiles.All().Select(profile => profile.Name);
        public string? SelectedProfile
        {
            get => _selectedProfile;
            set => SetField(ref _selectedProfile, value);
        }
        public string EditText
        {
            get => _editText;
            set
            {
                if (IsPositionEditor && int.TryParse(value, out var number))
                {
                    var max = GetPositionMaximum();
                    value = Math.Clamp(number, 1, Math.Max(1, max)).ToString();
                }
                SetField(ref _editText, value);
            }
        }

        public void SetLibraryProfileCompanionVisible(bool visible)
        {
            if (_isLibraryProfileCompanionVisible == visible) return;
            _isLibraryProfileCompanionVisible = visible;
            OnPropertyChanged(nameof(ShowAddToProfile));
            OnPropertyChanged(nameof(ShowInsert));
        }

        // The Shell supplies this only when a Library/Profile page is paired
        // with a Mod-details page. The BottomBar remains UI-neutral: it merely
        // derives whether the current selection can target that host.
        public void SetRelationshipHostContext(string? hostId, string? hostName)
        {
            var normalizedId = string.IsNullOrWhiteSpace(hostId) ? null : hostId.Trim();
            var normalizedName = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim();
            if (string.Equals(_relationshipHostId, normalizedId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_relationshipHostName, normalizedName, StringComparison.Ordinal))
                return;

            _relationshipHostId = normalizedId;
            _relationshipHostName = normalizedName;
            RefreshState();
        }

        public void SetSelectionActions(object content)
        {
            ArgumentNullException.ThrowIfNull(content);
            Registrations.Upsert(new BottomBarRegistrationRequest(
                "selection-actions",
                [new BottomBarRowDefinition("main", content)]));
        }

        public void ClearSelectionActions() => Registrations.Remove("selection-actions");

        public void SetTemporaryEditor(object content)
        {
            ArgumentNullException.ThrowIfNull(content);
            Registrations.Upsert(new BottomBarRegistrationRequest(
                "temporary-editor",
                [new BottomBarRowDefinition("main", content)]));
        }

        public void ClearTemporaryEditor() => Registrations.Remove("temporary-editor");

        public void BeginNameEdit(string modId, string currentValue) => BeginEdit(modId, "Name", currentValue);
        public void BeginDescriptionEdit(string modId, string currentValue) => BeginEdit(modId, "Description", currentValue);
        public void BeginCreateProfile() => BeginEdit(null, "CreateProfile", string.Empty);
        public void BeginCreateLogicalMod() => BeginEdit(null, "CreateLogicalMod", string.Empty);
        public void BeginRenameProfile() => BeginEdit(null, "RenameProfile", _profiles.SelectedKey ?? string.Empty);
        public void BeginSwitchProfile()
        {
            _editModId = null;
            _editMode = "SwitchProfile";
            SelectedProfile = _profiles.SelectedKey;
            _editText = string.Empty;
            RefreshState();
        }

        public void BeginMove()
        {
            if (IsProfileSelection && _profiles.SelectedProfile is { } profile)
            {
                BeginPositionEdit(
                    "Move",
                    _selection.SelectedIds,
                    _profiles.GetSortedEntries(profile).Select(entry => entry.NodeId.Value.ToString("N")));
                return;
            }

            if (TryGetSelectedDirectHostOptions(out var hostId, out var orderedOptions))
                BeginPositionEdit("OptionMove", _selection.SelectedIds, orderedOptions, hostId);
        }

        public void BeginInsert()
        {
            if (!IsLibrarySelection || _profiles.SelectedProfile is not { } profile) return;
            BeginPositionEdit(
                "Insert",
                _selection.SelectedIds,
                _profiles.GetSortedEntries(profile).Select(entry => entry.NodeId.Value.ToString("N")));
        }

        public void CancelEdit()
        {
            if (!HasTemporaryEditor) return;
            EndEditWithoutRefresh();
            RefreshState();
        }

        private void EndEditWithoutRefresh()
        {
            _editModId = null;
            _editMode = null;
            _editHostId = null;
            _editText = string.Empty;
            SelectedProfile = null;
            OnPropertyChanged(nameof(EditText));
        }

        private void BeginEdit(string? modId, string mode, string currentValue)
        {
            _editModId = modId;
            _editMode = mode;
            _editText = currentValue ?? string.Empty;
            OnPropertyChanged(nameof(EditText));
            RefreshState();
        }

        private void BeginPositionEdit(
            string mode,
            IEnumerable<string> ids,
            IEnumerable<string> scopeOrder,
            string? hostId = null)
        {
            var requestedIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();
            var selected = requestedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orderedIds = scopeOrder
                .Where(selected.Contains)
                .Concat(requestedIds.Where(selected.Contains))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (orderedIds.Length == 0) return;

            _editModId = string.Join("|", orderedIds);
            _editMode = mode;
            _editHostId = hostId;
            _editText = string.Empty;
            _suppressSelectionCancel = true;
            try { _selection.Clear(); }
            finally { _suppressSelectionCancel = false; }
            OnPropertyChanged(nameof(EditText));
            RefreshState();
        }

        private async Task ConfirmEditAsync()
        {
            if (_editMode == "SwitchProfile")
            {
                if (!string.IsNullOrWhiteSpace(SelectedProfile)) _profiles.Select(SelectedProfile);
                EndEditWithoutRefresh();
                RefreshState();
                _refresh();
                return;
            }
            if (_editMode == "CreateProfile")
            {
                await _profiles.CreateNewAsync(EditText);
                EndEditWithoutRefresh();
                RefreshState();
                _refresh();
                return;
            }
            if (_editMode == "CreateLogicalMod")
            {
                if (string.IsNullOrWhiteSpace(EditText)) return;
                try
                {
                    var created = await _library.CreateLogicalContainerAsync(EditText.Trim());
                    _notifications.Show($"已新建逻辑 Mod：{created.Name}");
                    EndEditWithoutRefresh();
                    RefreshState();
                    _refresh();
                }
                catch (Exception exception)
                {
                    _notifications.Show($"新建逻辑 Mod 失败：{exception.Message}", NotificationLevel.Error);
                }
                return;
            }
            if (_editMode == "RenameProfile")
            {
                var oldName = _profiles.SelectedKey;
                if (!string.IsNullOrWhiteSpace(oldName) && !string.IsNullOrWhiteSpace(EditText))
                    await _profiles.RenameAsync(oldName, EditText.Trim());
                EndEditWithoutRefresh();
                RefreshState();
                _refresh();
                return;
            }
            if (IsPositionEditor)
            {
                await ConfirmPositionEditAsync();
                return;
            }
            if (string.IsNullOrWhiteSpace(_editModId) || string.IsNullOrWhiteSpace(_editMode)) return;
            var mod = _library.Get(_editModId);
            if (mod is null) { CancelEdit(); return; }
            if (string.Equals(_editMode, "Name", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(EditText)) return;
                if (!await _library.RenameAsync(mod.Guid, EditText.Trim())) return;
                _notifications.Show($"已重命名：{EditText.Trim()}");
            }
            else
            {
                mod.Description = EditText;
                if (!await _library.AddAsync(mod)) return;
                _notifications.Show($"已更新备注：{mod.Name}");
            }
            CancelEdit();
            _refresh();
        }

        private async Task ConfirmPositionEditAsync()
        {
            if (!int.TryParse(EditText?.Trim(), out var requested)) return;
            var ids = (_editModId ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (_editMode == "OptionMove")
            {
                await ConfirmOptionPositionEditAsync(ids, requested);
                return;
            }

            var profile = _profiles.SelectedProfile;
            if (profile is null || ids.Count == 0) { CancelEdit(); return; }
            var ordered = _profiles.GetSortedEntries(profile).Select(entry => entry.NodeId.Value.ToString("N")).ToList();
            if (_editMode == "Move")
            {
                var max = ordered.Count;
                var target = Math.Clamp(requested, 1, max);
                var selected = ids.Where(ordered.Contains).ToList();
                ordered.RemoveAll(selected.Contains);
                target = Math.Min(target, ordered.Count + 1);
                ordered.InsertRange(target - 1, selected);
            }
            else
            {
                var target = Math.Clamp(requested, 1, ordered.Count + 1);
                var additions = ids.Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                ordered.InsertRange(target - 1, additions);
            }
            if (await _profiles.ReplaceSelectedEntriesAsync(ordered))
            {
                _notifications.Show(_editMode == "Move" ? "已移动配置项。" : "已插入配置项。");
                CancelEdit();
            }
        }

        private async Task ConfirmOptionPositionEditAsync(IReadOnlyList<string> ids, int requested)
        {
            if (string.IsNullOrWhiteSpace(_editHostId) || ids.Count == 0)
            {
                CancelEdit();
                return;
            }

            var ordered = _library.GetOptionsForHost(_editHostId)
                .Select(option => option.Guid)
                .ToList();
            var optionSet = ordered.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = ids
                .Where(optionSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selected.Count == 0)
            {
                CancelEdit();
                return;
            }

            var target = Math.Clamp(requested, 1, ordered.Count);
            var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ordered.RemoveAll(selectedSet.Contains);
            target = Math.Min(target, ordered.Count + 1);
            ordered.InsertRange(target - 1, selected);
            if (!await _library.ReplaceDirectOptionOrderAsync(_editHostId, ordered))
            {
                CancelEdit();
                return;
            }

            _notifications.Show("已调整选项顺序。");
            CancelEdit();
            _refresh();
        }

        private int GetPositionMaximum()
        {
            if (_editMode == "OptionMove")
                return string.IsNullOrWhiteSpace(_editHostId) ? 0 : _library.GetOptionsForHost(_editHostId).Count;

            var entryCount = _profiles.SelectedProfile?.Entries.Count ?? 0;
            return _editMode == "Insert" ? entryCount + 1 : entryCount;
        }

        private bool TryGetSelectedDirectHostOptions(out string hostId, out IReadOnlyList<string> orderedOptions)
        {
            hostId = string.Empty;
            orderedOptions = Array.Empty<string>();
            if (!IsDecorationHostSelection || string.IsNullOrWhiteSpace(_selection.Scope)) return false;

            hostId = _selection.Scope["DecorationHost:".Length..];
            if (string.IsNullOrWhiteSpace(hostId)) return false;

            var current = _library.GetOptionsForHost(hostId)
                .Select(option => option.Guid)
                .ToArray();
            if (current.Length == 0 || _selection.SelectedIds.Count == 0) return false;

            var currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!_selection.SelectedIds.All(currentSet.Contains)) return false;

            orderedOptions = current;
            return true;
        }

        private IReadOnlyList<string> GetSelectedOptionCandidateIds()
        {
            if (!IsRelationshipSourceSelection || string.IsNullOrWhiteSpace(_relationshipHostId))
                return Array.Empty<string>();

            return _selection.SelectedIds
                .Where(id => !string.Equals(id, _relationshipHostId, StringComparison.OrdinalIgnoreCase))
                .Where(id => _library.Get(id) is { } mod && !mod.IsDecoration && !mod.IsReShade)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private HD2ModManager.Models.ModEntity? GetRelationshipHost()
        {
            if (string.IsNullOrWhiteSpace(_relationshipHostId)) return null;
            var host = _library.Get(_relationshipHostId);
            return host is null || host.IsDecoration || host.IsReShade ? null : host;
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasTemporaryEditor));
            OnPropertyChanged(nameof(IsPositionEditor));
            OnPropertyChanged(nameof(IsProfileSwitchEditor));
            OnPropertyChanged(nameof(IsTextEditor));
            OnPropertyChanged(nameof(ProfileOptions));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(IsLibrarySelection));
            OnPropertyChanged(nameof(IsProfileSelection));
            OnPropertyChanged(nameof(IsDecorationHostSelection));
            OnPropertyChanged(nameof(IsRelationshipSourceSelection));
            OnPropertyChanged(nameof(RelationshipHostId));
            OnPropertyChanged(nameof(RelationshipHostName));
            OnPropertyChanged(nameof(ShowAddToProfile));
            OnPropertyChanged(nameof(ShowSetAsOption));
            OnPropertyChanged(nameof(ShowAttachDecorationsToHost));
            OnPropertyChanged(nameof(ShowEnableDecorations));
            OnPropertyChanged(nameof(ShowDisableDecorations));
            OnPropertyChanged(nameof(ShowDelete));
            OnPropertyChanged(nameof(ShowRemove));
            OnPropertyChanged(nameof(ShowRemoveOptionsFromHost));
            OnPropertyChanged(nameof(ShowDeleteFromLibrary));
            OnPropertyChanged(nameof(ShowMove));
            OnPropertyChanged(nameof(ShowInsert));
            OnPropertyChanged(nameof(MoveButtonText));
            OnPropertyChanged(nameof(SetAsOptionText));
            OnPropertyChanged(nameof(AttachDecorationsToHostText));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(EditLabel));
            OnPropertyChanged(nameof(PositionLabel));
            OnPropertyChanged(nameof(PositionHint));
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
