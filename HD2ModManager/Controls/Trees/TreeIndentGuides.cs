using System;
using System.Windows;
using System.Windows.Media;

namespace HD2ModManager.Controls.Trees;

/// <summary>
/// 缩进 + 层级竖线。行模板里放一个就行：宽度按 <see cref="Depth"/> 自己算，
/// 竖线也按 <see cref="Depth"/> 自己画。调用方不用管"第几层缩进多少像素"这种布局知识。
///
/// 画法是旧的简单树那一套：**每一层一条贯穿整行的竖线**，没有拐角、没有横勾。
/// 一行在深度 d 就画 d 条平行线，位置固定；层级深了也只是多一条线，不会出现
/// ├ / └ 那种逐行变形的图案。
/// </summary>
public sealed class TreeIndentGuides : FrameworkElement
{
    /// <summary>一层的缩进宽度，也是相邻两条竖线的间距。</summary>
    public static readonly DependencyProperty IndentWidthProperty = DependencyProperty.Register(
        nameof(IndentWidth), typeof(double), typeof(TreeIndentGuides),
        new FrameworkPropertyMetadata(TreeMetrics.IndentWidth, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
        nameof(Depth), typeof(int), typeof(TreeIndentGuides),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>线条颜色。不设时用主题的 StrokeSubtle。</summary>
    public static readonly DependencyProperty GuideBrushProperty = DependencyProperty.Register(
        nameof(GuideBrush), typeof(Brush), typeof(TreeIndentGuides),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>行高：竖线要按行高画满，否则相邻两行之间会断开一截。</summary>
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(TreeIndentGuides),
        new FrameworkPropertyMetadata(TreeMetrics.RowHeight, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double IndentWidth { get => (double)GetValue(IndentWidthProperty); set => SetValue(IndentWidthProperty, value); }
    public int Depth { get => (int)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }
    public Brush? GuideBrush { get => (Brush?)GetValue(GuideBrushProperty); set => SetValue(GuideBrushProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    private Pen? _pen;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(0, Depth) * IndentWidth;
        // 高度跟着行高走：竖线要画满整行，否则相邻两行之间会断开一截。
        var height = double.IsInfinity(availableSize.Height) ? RowHeight : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var depth = Math.Max(0, Depth);
        if (depth <= 0) return;

        var height = RenderSize.Height > 0 ? RenderSize.Height : RowHeight;
        if (height <= 0) return;

        var pen = ResolvePen();
        for (var column = 0; column < depth; column++)
        {
            // 竖线画在两级缩进的正中间：第 i 层的灰条左边缘在 i×缩进，第 i+1 层在 (i+1)×缩进，
            // 中线就是 (i+0.5)×缩进。旧树也是这个位置（它的行容器 8 外边距 + 8 内边距）。
            // 注意别画到 (i+1)×缩进 上——那正好是子级灰条的左边缘，看起来像贴着子级的框。
            var x = (column + 0.5) * IndentWidth;
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
    }

    private Pen ResolvePen()
    {
        if (_pen is not null) return _pen;
        var brush = GuideBrush
            ?? TryFindResource("StrokeSubtle") as Brush
            ?? Brushes.LightGray;
        var pen = new Pen(brush, 1d);
        pen.Freeze();
        _pen = pen;
        return _pen;
    }
}
