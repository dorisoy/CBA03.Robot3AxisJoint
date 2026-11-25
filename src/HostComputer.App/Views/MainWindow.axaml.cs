using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HostComputer.App.Controls;
using HostComputer.App.ViewModels;
using System.Text;

namespace HostComputer.App.Views;

public partial class MainWindow : Window
{
    private bool closing;
    private bool closedSafely;
    private MainViewModel? attached;
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateHeaderLayout();
        UpdateHeaderLayout();
        DataContextChanged += (_, _) =>
        {
            if (attached != null) attached.CurveChanged -= RefreshPlot;
            attached = DataContext as MainViewModel;
            if (attached != null) attached.CurveChanged += RefreshPlot;
        };
        Closing += async (_, e) =>
        {
            if (closedSafely) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            if (attached != null)
            {
                attached.Message = "正在撤销授权并有界确认停止输出…";
                await attached.DisposeAsync();
            }
            closedSafely = true;
            Close();
        };
    }
    private void UpdateHeaderLayout()
    {
        var toolbar = this.FindControl<StackPanel>("ConnectionToolbar")!;
        var actions = this.FindControl<StackPanel>("HeaderActions")!;
        var narrow = (Bounds.Width > 0 ? Bounds.Width : Width) < 1500;
        Grid.SetColumn(actions, narrow ? 1 : 2);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1);
        Grid.SetRow(toolbar, narrow ? 1 : 0);
        Grid.SetColumn(toolbar, narrow ? 0 : 1);
        Grid.SetColumnSpan(toolbar, narrow ? 3 : 1);
        toolbar.Margin = narrow ? new Avalonia.Thickness(0, 8, 0, 0) : new Avalonia.Thickness(12, 0);
    }
    private void RefreshPlot() => this.FindControl<TelemetryPlot>("Plot")?.Update(attached!.Curve.Points, attached.CurveMode);
    private async Task ExportAsync(string extension, string content)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "显式导出运行记录",
                SuggestedFileName = $"CBA03-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = ["*." + extension] }]
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(content);
            attached!.Message = "已导出：" + file.Name;
        }
        catch (Exception ex) { if (attached != null) attached.Message = "导出失败：" + ex.Message; }
    }
    private async void ExportCsv(object? sender, RoutedEventArgs e)
    { if (attached != null) await ExportAsync("csv", attached.Curve.ToCsv()); }
    private async void ExportJson(object? sender, RoutedEventArgs e)
    { if (attached != null) await ExportAsync("json", attached.ExportParameters()); }
    private async void ShowHelp(object? sender, RoutedEventArgs e)
    {
        if (attached == null) return;
        var help = new Window
        {
            Title = "操作与安全边界",
            Width = 760,
            Height = 580,
            Content = new ScrollViewer { Content = new TextBlock { Text = attached.HelpText, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24), LineHeight = 26 } }
        };
        await help.ShowDialog(this);
    }
}
