using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HD2ModManager.Views
{
    // 作用：封装三段式胶囊按钮，统一控制整体圆角与两侧圆形按钮尺寸。
    public partial class CapsuleButtonGroup : UserControl
    {
        public static readonly DependencyProperty CapsuleBackgroundProperty = DependencyProperty.Register(nameof(CapsuleBackground), typeof(Brush), typeof(CapsuleButtonGroup), new PropertyMetadata(Brushes.Transparent));
        public static readonly DependencyProperty CapsuleBorderBrushProperty = DependencyProperty.Register(nameof(CapsuleBorderBrush), typeof(Brush), typeof(CapsuleButtonGroup), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(217, 222, 232))));
        public static readonly DependencyProperty CapsuleBorderThicknessProperty = DependencyProperty.Register(nameof(CapsuleBorderThickness), typeof(Thickness), typeof(CapsuleButtonGroup), new PropertyMetadata(new Thickness(1)));
        public static readonly DependencyProperty CapsulePaddingProperty = DependencyProperty.Register(nameof(CapsulePadding), typeof(Thickness), typeof(CapsuleButtonGroup), new PropertyMetadata(new Thickness(3)));
        public static readonly DependencyProperty CapsuleCornerRadiusProperty = DependencyProperty.Register(nameof(CapsuleCornerRadius), typeof(CornerRadius), typeof(CapsuleButtonGroup), new PropertyMetadata(new CornerRadius(20)));
        public static readonly DependencyProperty ButtonCornerRadiusProperty = DependencyProperty.Register(nameof(ButtonCornerRadius), typeof(CornerRadius), typeof(CapsuleButtonGroup), new PropertyMetadata(new CornerRadius(17)));
        public static readonly DependencyProperty ButtonHeightProperty = DependencyProperty.Register(nameof(ButtonHeight), typeof(double), typeof(CapsuleButtonGroup), new PropertyMetadata(34d));
        public static readonly DependencyProperty SideButtonSizeProperty = DependencyProperty.Register(nameof(SideButtonSize), typeof(double), typeof(CapsuleButtonGroup), new PropertyMetadata(40d));
        public static readonly DependencyProperty MiddleMinWidthProperty = DependencyProperty.Register(nameof(MiddleMinWidth), typeof(double), typeof(CapsuleButtonGroup), new PropertyMetadata(150d));

        public static readonly DependencyProperty LeftContentProperty = DependencyProperty.Register(nameof(LeftContent), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty MiddleContentProperty = DependencyProperty.Register(nameof(MiddleContent), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty RightContentProperty = DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(CapsuleButtonGroup));

        public static readonly DependencyProperty LeftCommandProperty = DependencyProperty.Register(nameof(LeftCommand), typeof(ICommand), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty MiddleCommandProperty = DependencyProperty.Register(nameof(MiddleCommand), typeof(ICommand), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty RightCommandProperty = DependencyProperty.Register(nameof(RightCommand), typeof(ICommand), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty LeftCommandParameterProperty = DependencyProperty.Register(nameof(LeftCommandParameter), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty MiddleCommandParameterProperty = DependencyProperty.Register(nameof(MiddleCommandParameter), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty RightCommandParameterProperty = DependencyProperty.Register(nameof(RightCommandParameter), typeof(object), typeof(CapsuleButtonGroup));

        public static readonly DependencyProperty LeftToolTipProperty = DependencyProperty.Register(nameof(LeftToolTip), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty MiddleToolTipProperty = DependencyProperty.Register(nameof(MiddleToolTip), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty RightToolTipProperty = DependencyProperty.Register(nameof(RightToolTip), typeof(object), typeof(CapsuleButtonGroup));
        public static readonly DependencyProperty LeftIsActiveProperty = DependencyProperty.Register(nameof(LeftIsActive), typeof(bool), typeof(CapsuleButtonGroup), new PropertyMetadata(false, OnActiveZoneChanged));
        public static readonly DependencyProperty MiddleIsActiveProperty = DependencyProperty.Register(nameof(MiddleIsActive), typeof(bool), typeof(CapsuleButtonGroup), new PropertyMetadata(false, OnActiveZoneChanged));
        public static readonly DependencyProperty RightIsActiveProperty = DependencyProperty.Register(nameof(RightIsActive), typeof(bool), typeof(CapsuleButtonGroup), new PropertyMetadata(false, OnActiveZoneChanged));

        // 胶囊的激活分区会变；宿主靠它把高亮块移过去。
        // 没有这个事件，宿主只能靠 DP 轮询或布局回调，既贵又容易漏。
        public event EventHandler? ActiveZoneChanged;

        private static void OnActiveZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((CapsuleButtonGroup)d).ActiveZoneChanged?.Invoke(d, EventArgs.Empty);

        public CapsuleButtonGroup()
        {
            InitializeComponent();
            SizeChanged += (_, _) => UpdateShapeMetrics();
            Loaded += (_, _) => UpdateShapeMetrics();
        }

        public Brush CapsuleBackground { get => (Brush)GetValue(CapsuleBackgroundProperty); set => SetValue(CapsuleBackgroundProperty, value); }
        public Brush CapsuleBorderBrush { get => (Brush)GetValue(CapsuleBorderBrushProperty); set => SetValue(CapsuleBorderBrushProperty, value); }
        public Thickness CapsuleBorderThickness { get => (Thickness)GetValue(CapsuleBorderThicknessProperty); set => SetValue(CapsuleBorderThicknessProperty, value); }
        public Thickness CapsulePadding { get => (Thickness)GetValue(CapsulePaddingProperty); set => SetValue(CapsulePaddingProperty, value); }
        public CornerRadius CapsuleCornerRadius { get => (CornerRadius)GetValue(CapsuleCornerRadiusProperty); private set => SetValue(CapsuleCornerRadiusProperty, value); }
        public CornerRadius ButtonCornerRadius { get => (CornerRadius)GetValue(ButtonCornerRadiusProperty); private set => SetValue(ButtonCornerRadiusProperty, value); }
        public double ButtonHeight { get => (double)GetValue(ButtonHeightProperty); private set => SetValue(ButtonHeightProperty, value); }
        public double SideButtonSize { get => (double)GetValue(SideButtonSizeProperty); private set => SetValue(SideButtonSizeProperty, value); }
        public double MiddleMinWidth { get => (double)GetValue(MiddleMinWidthProperty); set => SetValue(MiddleMinWidthProperty, value); }

        public object? LeftContent { get => GetValue(LeftContentProperty); set => SetValue(LeftContentProperty, value); }
        public object? MiddleContent { get => GetValue(MiddleContentProperty); set => SetValue(MiddleContentProperty, value); }
        public object? RightContent { get => GetValue(RightContentProperty); set => SetValue(RightContentProperty, value); }

        public ICommand? LeftCommand { get => (ICommand?)GetValue(LeftCommandProperty); set => SetValue(LeftCommandProperty, value); }
        public ICommand? MiddleCommand { get => (ICommand?)GetValue(MiddleCommandProperty); set => SetValue(MiddleCommandProperty, value); }
        public ICommand? RightCommand { get => (ICommand?)GetValue(RightCommandProperty); set => SetValue(RightCommandProperty, value); }
        public object? LeftCommandParameter { get => GetValue(LeftCommandParameterProperty); set => SetValue(LeftCommandParameterProperty, value); }
        public object? MiddleCommandParameter { get => GetValue(MiddleCommandParameterProperty); set => SetValue(MiddleCommandParameterProperty, value); }
        public object? RightCommandParameter { get => GetValue(RightCommandParameterProperty); set => SetValue(RightCommandParameterProperty, value); }

        public object? LeftToolTip { get => GetValue(LeftToolTipProperty); set => SetValue(LeftToolTipProperty, value); }
        public object? MiddleToolTip { get => GetValue(MiddleToolTipProperty); set => SetValue(MiddleToolTipProperty, value); }
        public object? RightToolTip { get => GetValue(RightToolTipProperty); set => SetValue(RightToolTipProperty, value); }
        public bool LeftIsActive { get => (bool)GetValue(LeftIsActiveProperty); set => SetValue(LeftIsActiveProperty, value); }
        public bool MiddleIsActive { get => (bool)GetValue(MiddleIsActiveProperty); set => SetValue(MiddleIsActiveProperty, value); }
        public bool RightIsActive { get => (bool)GetValue(RightIsActiveProperty); set => SetValue(RightIsActiveProperty, value); }

        public FrameworkElement? GetActiveButton()
        {
            if (LeftIsActive) return LeftButton;
            if (MiddleIsActive) return MiddleButton;
            return RightIsActive ? RightButton : null;
        }

        private void UpdateShapeMetrics()
        {
            var height = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(height) || height <= 0) return;

            var radius = height / 2d;
            CapsuleCornerRadius = new CornerRadius(radius);
            ButtonHeight = Math.Max(0, height - CapsulePadding.Top - CapsulePadding.Bottom - CapsuleBorderThickness.Top - CapsuleBorderThickness.Bottom);
            ButtonCornerRadius = new CornerRadius(ButtonHeight / 2d);
            SideButtonSize = ButtonHeight;
        }
    }
}
