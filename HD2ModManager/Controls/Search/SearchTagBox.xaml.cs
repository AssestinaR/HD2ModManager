using System;
using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HD2ModManager.ViewModels;

namespace HD2ModManager.Controls.Search
{
    /// <summary>
    /// 标签式搜索框：<see cref="Tags"/> 是**只读投影**（谁提供谁负责增删），
    /// 用户意图（删一枚标签、把草稿提交成标签）通过事件交回宿主，
    /// 本控件自己不改任何筛选状态。
    ///
    /// 这么分是为了只有一个事实源：分类选择在 ModCategoryFilter 里、宿主开关在页面 VM 里、
    /// 自由标签在 ModListPanel 里。控件如果自己也存一份，就会出现"胶囊还在、筛选早没了"。
    /// </summary>
    public partial class SearchTagBox : UserControl
    {
        // 输入区保底宽度：标签再多也不能把输入框挤没，否则用户点不到光标。
        private const double InputMinWidth = 96;

        public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
            nameof(Tags), typeof(IEnumerable), typeof(SearchTagBox), new PropertyMetadata(null, OnTagsChanged));

        public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(
            nameof(Draft), typeof(string), typeof(SearchTagBox),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        private bool _syncingDraft;
        private INotifyCollectionChanged? _observedTags;

        public SearchTagBox()
        {
            InitializeComponent();
            SizeChanged += (_, _) => UpdateTagAreaWidth();
            TagScroller.SizeChanged += (_, _) => UpdateTagAreaWidth();
        }

        /// <summary>草稿：输入框里还没提交成标签的那段文字。由宿主写、由用户改。</summary>
        public string Draft
        {
            get => (string)GetValue(DraftProperty);
            set => SetValue(DraftProperty, value);
        }

        /// <summary>要显示的标签（只读投影）。</summary>
        public IEnumerable? Tags
        {
            get => (IEnumerable?)GetValue(TagsProperty);
            set => SetValue(TagsProperty, value);
        }

        /// <summary>用户点了某一枚标签（= 想撤销它）。</summary>
        public event EventHandler<ModListSearchTag>? TagRemoveRequested;

        /// <summary>用户按空格/回车，想把草稿提交成标签。草稿非空时才会触发。</summary>
        public event EventHandler? CommitRequested;

        /// <summary>草稿被用户改动（程序写 Draft 不会触发）。</summary>
        public event EventHandler? DraftChanged;

        public void FocusEditor()
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            InputBox.CaretIndex = InputBox.Text.Length;
        }

        public void SelectAllText()
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }

        public bool IsEditorFocused => InputBox.IsKeyboardFocusWithin;

        /// <summary>搜索框收起时把它从 Tab 序列里摘掉，免得 Tab 键走进去打字却看不见。</summary>
        public bool EditorTabStop
        {
            get => InputBox.IsTabStop;
            set => InputBox.IsTabStop = value;
        }

        /// <summary>把标签区滚到最右：新加的那一枚永远看得见。</summary>
        public void ScrollTagsToEnd() => TagScroller.ScrollToRightEnd();

        private static void OnTagsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
        {
            if (target is not SearchTagBox box) return;

            if (box._observedTags is not null) box._observedTags.CollectionChanged -= box.OnTagsCollectionChanged;
            box._observedTags = args.NewValue as INotifyCollectionChanged;
            if (box._observedTags is not null) box._observedTags.CollectionChanged += box.OnTagsCollectionChanged;

            box.UpdateTagAreaWidth();
        }

        private void OnTagsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add)
            {
                // 新增的标签默认在最右端：不等布局跑完就滚过去，用户看到的和真实状态一致。
                Dispatcher.BeginInvoke(new Action(() => TagScroller.ScrollToRightEnd()),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            UpdateTagAreaWidth();
        }

        // 标签区宽度 = 整个框 - 输入区保底。Auto 列配上这个上限，标签区才会在够用时收缩、
        // 不够用时横向滚动，而不是把输入框一路挤成 0。
        private void UpdateTagAreaWidth()
        {
            if (!IsLoaded && ActualWidth <= 0) return;

            var available = ActualWidth - 2 /*边框*/ - InputMinWidth;
            TagScroller.MaxWidth = Math.Max(0, available);
        }

        private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 点在标签区空白处（不是某枚标签）时，把焦点交给输入框。
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
            FocusEditor();
        }

        private void OnTagClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: ModListSearchTag tag }) return;
            e.Handled = true;
            TagRemoveRequested?.Invoke(this, tag);
        }

        private void OnTagScrollerMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;
            if (TagScroller.ScrollableWidth <= 0) return;
            TagScroller.ScrollToHorizontalOffset(TagScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingDraft) return;

            _syncingDraft = true;
            try { SetCurrentValue(DraftProperty, InputBox.Text); }
            finally { _syncingDraft = false; }

            DraftChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property != DraftProperty || _syncingDraft) return;

            // 外部（宿主）改了草稿：同步到输入框，并把光标放到末尾。
            _syncingDraft = true;
            try
            {
                var text = Draft ?? string.Empty;
                if (InputBox.Text != text)
                {
                    InputBox.Text = text;
                    InputBox.CaretIndex = text.Length;
                }
            }
            finally { _syncingDraft = false; }
        }

        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 输入法组合中：Key 会是 ImeProcessed（不是 Space），这时什么都不做，
            // 让输入法把这一下吃掉——否则中文选词会被当成"提交标签"。
            if (e.Key == Key.ImeProcessed) return;

            if (e.Key == Key.Back && InputBox.Text.Length == 0)
            {
                RemoveLastTag();
                e.Handled = true;
                return;
            }

            if (e.Key is not (Key.Space or Key.Enter)) return;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (string.IsNullOrWhiteSpace(InputBox.Text)) return;

            CommitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void RemoveLastTag()
        {
            if (Tags is null) return;
            ModListSearchTag? last = null;
            foreach (var item in Tags)
            {
                if (item is ModListSearchTag tag) last = tag;
            }

            if (last is not null) TagRemoveRequested?.Invoke(this, last);
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            for (var current = node; current is not null; current = VisualTreeWalk.Parent(current))
            {
                if (current is T match) return match;
            }

            return null;
        }
    }
}
