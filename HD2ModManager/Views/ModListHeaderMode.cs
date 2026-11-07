using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HD2ModManager.Views
{
    /// <summary>
    /// 标题卡的一个"可切换状态"（第三种、第四种……模式）。
    ///
    /// 面板自己只内置两种：标题、搜索。除此之外想占用这块位置的引用方（比如模组库页和配置页的
    /// 分类芯片条）注册一个 <see cref="ModListHeaderMode"/>，面板负责互斥、交叉淡入淡出，
    /// 并按这里给的图标与提示生成切换按钮——面板不需要知道"分类"是什么。
    ///
    /// 做成 <see cref="ContentControl"/> 而不是普通对象，是为了让它进视觉树后能继承 DataContext，
    /// 引用方才能直接写 <c>IsRequested="{Binding Category.IsEnabled, Mode=TwoWay}"</c>；
    /// 内容就是它自己的 Content。
    ///
    /// 两个开关分开表达，是因为它们由不同的人决定：
    /// - <see cref="IsRequested"/>：用户想不想看它（宿主持久化；点切换按钮时由面板翻转）；
    /// - <see cref="IsAvailable"/>：此刻它有没有内容可显示（例如"已经有分类芯片了"）。
    /// 两者都成立才真的占位，所以不会出现"打开了却一片空白"。
    /// </summary>
    public sealed class ModListHeaderMode : ContentControl
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
            nameof(Key), typeof(string), typeof(ModListHeaderMode), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsRequestedProperty = DependencyProperty.Register(
            nameof(IsRequested), typeof(bool), typeof(ModListHeaderMode),
            new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
            nameof(IsAvailable), typeof(bool), typeof(ModListHeaderMode),
            new PropertyMetadata(true, OnStateChanged));

        public static readonly DependencyProperty ToggleToolTipProperty = DependencyProperty.Register(
            nameof(ToggleToolTip), typeof(string), typeof(ModListHeaderMode), new PropertyMetadata(null));

        public static readonly DependencyProperty ToggleIconProperty = DependencyProperty.Register(
            nameof(ToggleIcon), typeof(Geometry), typeof(ModListHeaderMode), new PropertyMetadata(null));

        /// <summary>IsRequested / IsAvailable 变化时通知面板重算当前占位者。</summary>
        public event EventHandler? StateChanged;

        /// <summary>稳定标识，用于程序化切换（见 <c>ModListPanel.SetHeaderMode</c>）。</summary>
        public string Key
        {
            get => (string)GetValue(KeyProperty);
            set => SetValue(KeyProperty, value);
        }

        public bool IsRequested
        {
            get => (bool)GetValue(IsRequestedProperty);
            set => SetValue(IsRequestedProperty, value);
        }

        public bool IsAvailable
        {
            get => (bool)GetValue(IsAvailableProperty);
            set => SetValue(IsAvailableProperty, value);
        }

        public bool IsActive => IsRequested && IsAvailable;

        /// <summary>面板在搜索键旁边给它生成的切换按钮：提示文字与图标。留空则只显示空按钮。</summary>
        public string? ToggleToolTip
        {
            get => (string?)GetValue(ToggleToolTipProperty);
            set => SetValue(ToggleToolTipProperty, value);
        }

        public Geometry? ToggleIcon
        {
            get => (Geometry?)GetValue(ToggleIconProperty);
            set => SetValue(ToggleIconProperty, value);
        }

        private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
            => (sender as ModListHeaderMode)?.StateChanged?.Invoke(sender, EventArgs.Empty);
    }
}
