using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HostComputer.App.ViewModels;

namespace HostComputer.App.Controls;

/// <summary>
/// 只转发真实输入事件后的最终值；不订阅 Value/绑定变化来发运动命令。
/// </summary>
public sealed class UserTargetSlider : Slider
{
    private bool dragging, releasing, posted;
    public static readonly DirectProperty<UserTargetSlider, double> MajorTickFrequencyProperty =
        AvaloniaProperty.RegisterDirect<UserTargetSlider, double>(nameof(MajorTickFrequency), s => s.MajorTickFrequency);
    public double MajorTickFrequency => TickFrequency * 5;
    protected override Type StyleKeyOverride => typeof(Slider);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 主刻度仅随显示刻度变化，不参与目标量化或命令提交。
        if (change.Property == TickFrequencyProperty)
            RaisePropertyChanged(MajorTickFrequencyProperty, change.GetOldValue<double>() * 5, MajorTickFrequency);
    }

    public UserTargetSlider()
    {
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            dragging = true; releasing = false; SubmitAfterInput();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => { if (dragging) SubmitAfterInput(); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => { releasing = true; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (dragging) SubmitAfterInput();
            dragging = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (dragging && !releasing)
            {
                dragging = false;
                if (DataContext is AxisViewModel axis) _ = axis.CancelUserDragAsync();
            }
        }, RoutingStrategies.Direct, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                SubmitAfterInput();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    private void SubmitAfterInput()
    {
        if (posted) return;
        posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            posted = false;
            if (IsEffectivelyEnabled && DataContext is AxisViewModel axis) axis.SubmitUserSliderValue(Value);
        }, DispatcherPriority.Input);
    }
}
