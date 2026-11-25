using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HostComputer.Core;
using System.Globalization;

namespace HostComputer.App.Controls;

/// <summary>
/// 自定义控件 AxisGauge，用于显示轴的实际值、目标值和预览值的仪表盘。该控件继承自 Avalonia 的 Control 类，提供了多个可绑定的属性，包括 Actual、Target、Preview、Accent、DisplayText、Minimum 和 Maximum。控件会根据这些属性绘制一个圆形仪表盘，显示当前轴的状态，并在底部显示相关的文本信息。
/// </summary>
public sealed class AxisGauge : Control
{
    public static readonly StyledProperty<double?> ActualProperty = AvaloniaProperty.Register<AxisGauge, double?>(nameof(Actual));
    public static readonly StyledProperty<double?> TargetProperty = AvaloniaProperty.Register<AxisGauge, double?>(nameof(Target));
    public static readonly StyledProperty<double?> PreviewProperty = AvaloniaProperty.Register<AxisGauge, double?>(nameof(Preview));
    public static readonly StyledProperty<IBrush> AccentProperty = AvaloniaProperty.Register<AxisGauge, IBrush>(nameof(Accent), Brushes.SkyBlue);
    public static readonly StyledProperty<string> DisplayTextProperty = AvaloniaProperty.Register<AxisGauge, string>(nameof(DisplayText), "");
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<AxisGauge, double>(nameof(Minimum), Protocol.JointMinimumDeg);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<AxisGauge, double>(nameof(Maximum), Protocol.JointMaximumDeg);
    public double? Actual { get => GetValue(ActualProperty); set => SetValue(ActualProperty, value); }
    public double? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public double? Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public IBrush Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public string DisplayText { get => GetValue(DisplayTextProperty); set => SetValue(DisplayTextProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    private const double PreferredSize = 280;
    private const double LegendHeight = 24;
    static AxisGauge() => AffectsRender<AxisGauge>(ActualProperty, TargetProperty, PreviewProperty, AccentProperty, DisplayTextProperty, MinimumProperty, MaximumProperty);
    protected override Size MeasureOverride(Size availableSize)
    {
        // 按轴卡片宽度保留近正方形表盘，图例单独占高，避免挤占刻度。
        var diameter = Math.Min(PreferredSize, availableSize.Width);
        return new Size(diameter, diameter + LegendHeight);
    }

    /// <summary>
    /// 呈现 AxisGauge 控件的视觉内容，包括圆形仪表盘、刻度线、实际值和目标值的指针、预览值的标记，以及底部的文本信息。控件根据 Actual、Target、Preview、Accent、DisplayText、Minimum 和 Maximum 属性的值动态绘制仪表盘，并根据控件的大小进行缩放和布局。
    /// </summary>
    /// <param name="ctx"></param>
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var center = new Point(Bounds.Width / 2, (Bounds.Height - LegendHeight) / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height - LegendHeight) / 2 - 8;
        if (radius < 10) return;
        var labelSize = Math.Clamp(radius / 9.5, 11, 14);
        ctx.DrawEllipse(Brush.Parse("#101923"), new Pen(Brush.Parse("#42566F"), 1.5), center, radius, radius);
        Point At(double angle, double length) => new(center.X + Math.Cos(angle) * length, center.Y + Math.Sin(angle) * length);
        double Angle(double value) => (150 + Math.Clamp((value - Minimum) / Math.Max(1e-6, Maximum - Minimum), 0, 1) * 240) * Math.PI / 180;
        for (var i = 0; i <= 24; i++)
        {
            var angle = (150 + i * 10) * Math.PI / 180;
            var major = i % 4 == 0;
            var length = major ? Math.Clamp(radius * .14, 12, 19) : Math.Clamp(radius * .075, 7, 10);
            ctx.DrawLine(new Pen(Brush.Parse(major ? "#A8BCD1" : "#42566F"), major ? 2.5 : 1.5), At(angle, radius - 5), At(angle, radius - 5 - length));
        }
        if (Target is { } target && double.IsFinite(target))
            ctx.DrawLine(new Pen(Brushes.White, 2.5, DashStyle.Dash), center, At(Angle(target), radius * .82));
        if (Actual is { } actual && double.IsFinite(actual))
            ctx.DrawLine(new Pen(Accent, 5, lineCap: PenLineCap.Round), center, At(Angle(actual), radius * .72));
        if (Preview is { } preview && double.IsFinite(preview) && preview >= Minimum && preview <= Maximum)
            ctx.DrawEllipse(Brushes.MediumSpringGreen, null, At(Angle(preview), radius * .91), 4, 4);
        ctx.DrawEllipse(Accent, null, center, 7, 7);
        ctx.DrawEllipse(Brush.Parse("#101923"), null, center, 3, 3);
        var labels = Brush.Parse("#A8BCD1");
        CenteredText(ctx, $"{Minimum:0.#}°", At(Angle(Minimum), radius * .62), labelSize, labels);
        CenteredText(ctx, $"{(Minimum + Maximum) / 2:0.#}°", At(Angle((Minimum + Maximum) / 2), radius * .62), labelSize, labels);
        CenteredText(ctx, $"{Maximum:0.#}°", At(Angle(Maximum), radius * .62), labelSize, labels);
        if (!string.IsNullOrEmpty(DisplayText))
        {
            // 原位置读数移入圆盘底部；窄表盘缩放文字，保留完整角度和未知状态。
            var typeface = new Typeface("Microsoft YaHei UI, Inter, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
            var size = Math.Min(34, radius * .26);
            FormattedText Readout(double fontSize) => new(DisplayText, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Accent);
            var text = Readout(size);
            var fit = Math.Min(1, Math.Min(radius * 1.16 / Math.Max(1, text.Width), radius * .30 / Math.Max(1, text.Height)));
            if (fit < 1) text = Readout(size * fit);
            ctx.DrawText(text, new Point(center.X - text.Width / 2, center.Y + radius * .64 - text.Height / 2));
        }
    }
    private static void CenteredText(DrawingContext ctx, string text, Point center, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Inter, sans-serif"), size, brush);
        ctx.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }
    internal static void Text(DrawingContext ctx, string text, Point origin, double size, IBrush brush) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Inter, sans-serif"), size, brush), origin);
}

/// <summary>
/// 遥测绘图控件 TelemetryPlot，用于显示三个节点的关节位置、速度或电流随时间变化的曲线图。该控件继承自 Avalonia 的 Control 类，提供了 Update 方法用于更新数据和绘制模式（位置、速度或电流）。控件会根据传入的数据绘制三条曲线，并在图表上显示网格线、坐标轴刻度和图例信息。
/// </summary>
public sealed class TelemetryPlot : Control
{
    private IReadOnlyList<CurvePoint> points = [];
    private int mode;
    private static readonly IBrush[] Colors = [Brush.Parse("#5AC8FA"), Brush.Parse("#BB9AF7"), Brush.Parse("#F3BD69")];
    private static readonly Pen[] ActualPens = Colors.Select(c => new Pen(c, 1.8)).ToArray();
    private static readonly Pen[] TargetPens = Colors.Select(c => new Pen(c, 1, DashStyle.Dash)).ToArray();
    private static readonly Pen HorizontalGrid = new(Brush.Parse("#253142"));
    private static readonly Pen VerticalGrid = new(Brush.Parse("#1C2836"));
    private CurvePoint[][] series = [[], [], []];

    /// <summary>
    /// 更新 TelemetryPlot 控件的数据和绘制模式。该方法接收一个只读列表的 CurvePoint 数据和一个整数表示绘制模式（0 表示位置，1 表示速度，2 表示电流）。如果传入的数据与当前数据相同且绘制模式未改变，则不进行任何操作。否则，控件会根据新的数据和模式重新计算每个节点的数据系列，并触发重绘操作。
    /// </summary>
    /// <param name="data"></param>
    /// <param name="metric"></param>
    public void Update(IReadOnlyList<CurvePoint> data, int metric)
    {
        if (ReferenceEquals(points, data) && mode == metric) return;
        points = data; mode = metric;
        series = Enumerable.Range(1, 3).Select(n => data.Where(p => p.Node == n).ToArray()).ToArray();
        InvalidateVisual();
    }

    /// <summary>
    /// 构建数据系列的几何图形，用于绘制曲线。根据传入的数据点、值选择函数和映射函数生成 StreamGeometry。
    /// </summary>
    /// <param name="data"></param>
    /// <param name="value"></param>
    /// <param name="map"></param>
    /// <returns></returns>
    private static StreamGeometry BuildSeries(CurvePoint[] data, Func<CurvePoint, double?> value, Func<double, double, Point> map)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        var open = false;
        var lastTime = double.NegativeInfinity;
        foreach (var point in data)
        {
            var v = value(point);
            if (!double.IsFinite(point.Time) || v is null || !double.IsFinite(v.Value))
            { if (open) path.EndFigure(false); open = false; continue; }
            if (open && point.Time - lastTime is < 0 or > .2)
            { path.EndFigure(false); open = false; }
            if (!open) { path.BeginFigure(map(point.Time, v.Value), false); open = true; }
            else path.LineTo(map(point.Time, v.Value));
            lastTime = point.Time;
        }
        if (open) path.EndFigure(false);
        return geometry;
    }

    /// <summary>
    /// 呈现 TelemetryPlot 控件的视觉内容，包括网格线、坐标轴刻度、三条曲线以及图例信息。控件根据 Update 方法传入的数据和绘制模式动态绘制曲线图，并根据控件的大小进行缩放和布局。
    /// </summary>
    /// <param name="ctx"></param>
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var area = new Rect(48, 25, Math.Max(1, Bounds.Width - 65), Math.Max(1, Bounds.Height - 50));
        double? Value(CurvePoint p) => mode == 1 ? p.Velocity : mode == 2 ? p.Current : p.Position;
        var values = points.Select(Value).Concat(mode == 0 ? points.Select(p => p.Target) : []).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        var min = values.Length > 0 ? Math.Min(-1, values.Min()) : mode == 0 ? Protocol.JointMinimumDeg : -15;
        var max = values.Length > 0 ? Math.Max(1, values.Max()) : mode == 0 ? Protocol.JointMaximumDeg : 15;
        var pad = (max - min) * .12; min -= pad; max += pad;
        var end = points.Count > 0 ? points[^1].Time : 60;
        Point Map(double time, double value) => new(area.X + (time - end + 60) / 60 * area.Width, area.Bottom - (value - min) / (max - min) * area.Height);
        for (var i = 0; i <= 4; i++)
        {
            var y = area.Y + i * area.Height / 4;
            ctx.DrawLine(HorizontalGrid, new(area.Left, y), new(area.Right, y));
            AxisGauge.Text(ctx, (max - i * (max - min) / 4).ToString("0.#"), new(4, y - 7), 10, Brushes.SlateGray);
        }
        for (var i = 0; i <= 6; i++)
        {
            var x = area.X + i * area.Width / 6;
            ctx.DrawLine(VerticalGrid, new(x, area.Top), new(x, area.Bottom));
            AxisGauge.Text(ctx, $"{(i - 6) * 10}s", new(x - 12, area.Bottom + 8), 10, Brushes.SlateGray);
        }
        using (ctx.PushClip(area))
        {
            for (var n = 1; n <= 3; n++)
            {
                // 每轴最多两条几何路径，保留缺失/过期断点，避免逐线段分配并提交绘图。
                ctx.DrawGeometry(null, ActualPens[n - 1], BuildSeries(series[n - 1], Value, Map));
                if (mode == 0) ctx.DrawGeometry(null, TargetPens[n - 1], BuildSeries(series[n - 1], p => p.Target, Map));
            }
        }
        for (var n = 1; n <= 3; n++) AxisGauge.Text(ctx, $"ID{n}", new(50 + n * 55, 4), 11, Colors[n - 1]);
        AxisGauge.Text(ctx, mode == 0 ? "实线 实际 / 虚线 目标 · 60s" : mode == 1 ? "输出轴速度 · 60s" : "Iq 实机反馈 · 60s", new(area.Right - 230, 4), 11, Brushes.SlateGray);
    }
}
