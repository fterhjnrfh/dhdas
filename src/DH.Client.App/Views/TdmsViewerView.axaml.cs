using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DH.Client.App.Controls;
using DH.Client.App.Data;
using System.IO;

namespace DH.Client.App.Views;

public partial class TdmsViewerView : UserControl
{
    public static readonly StyledProperty<string?> SelectedFileProperty =
        AvaloniaProperty.Register<TdmsViewerView, string?>(nameof(SelectedFile));
    public static readonly StyledProperty<int> SelectedDeviceIdProperty =
        AvaloniaProperty.Register<TdmsViewerView, int>(nameof(SelectedDeviceId), 0);
    public static readonly StyledProperty<OnlineChannelManager?> OnlineChannelManagerProperty =
        AvaloniaProperty.Register<TdmsViewerView, OnlineChannelManager?>(nameof(OnlineChannelManager));

    public string? SelectedFile
    {
        get => GetValue(SelectedFileProperty);
        set => SetValue(SelectedFileProperty, value);
    }

    public int SelectedDeviceId
    {
        get => GetValue(SelectedDeviceIdProperty);
        set => SetValue(SelectedDeviceIdProperty, value);
    }

    public OnlineChannelManager? OnlineChannelManager
    {
        get => GetValue(OnlineChannelManagerProperty);
        set => SetValue(OnlineChannelManagerProperty, value);
    }

    public TdmsViewerView()
    {
        InitializeComponent();
        var vm = new ViewModels.TdmsViewerViewModel();
        DataContext = vm;

        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        if (skView is not null)
        {
            // TDMS查看器使用离线数据的真实X值（秒），同时保留拖动和缩放交互。
            skView.UseTimeAxis = false;
            skView.UseDataXValues = true;
            skView.FormatXLabel = FormatSecondsLabel;
            skView.ScrollMode = false;   // 关闭滚动窗口，启用全范围与交互视口
            skView.DesiredXTicks = 10;  // 增加X轴刻度数，更精细
            skView.DesiredYTicks = 8;   // 增加Y轴刻度数
            skView.ShowLegend = true;   // 显示图例
            skView.UseExtremaAggregation = true; // 保留数据尖峰
            
            skView.DataProvider = () => vm.CurrentCurveData;
            skView.MultiChannelDataProvider = () => vm.CurrentMultiChannelData;
            skView.ChannelColorsMap = vm.ChannelColorsMap;
            vm.CurveDataUpdated += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                skView.ChannelColorsMap = vm.ChannelColorsMap;
                skView.InvalidateVisual();
            });

            // 视图状态记录：交互变化时写入到 VM
            skView.ViewStateChanged += (state) =>
            {
                vm.LastView = new ViewModels.TdmsViewerViewModel.ViewState
                {
                    ZoomX = state.ZoomX,
                    ViewLeft = state.ViewLeft,
                    ViewCount = state.ViewCount
                };
            };

            // 如存在上次视图状态，初始化应用
            if (vm.LastView is not null)
            {
                skView.SetViewState(new SkiaMultiChannelView.ViewState
                {
                    ZoomX = vm.LastView.ZoomX,
                    ViewLeft = vm.LastView.ViewLeft,
                    ViewCount = vm.LastView.ViewCount
                });
            }
        }

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == SelectedFileProperty)
                vm.SelectedFile = e.NewValue as string;
            else if (e.Property == SelectedDeviceIdProperty)
                vm.DeviceFilterId = (int)(e.NewValue ?? 0);
            else if (e.Property == OnlineChannelManagerProperty)
                vm.OnlineChannelManager = e.NewValue as OnlineChannelManager;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static string FormatSecondsLabel(double seconds)
    {
        double value = Math.Abs(seconds);
        if (value >= 1000)
        {
            return $"{seconds:0} s";
        }

        if (value >= 100)
        {
            return $"{seconds:0.0} s";
        }

        if (value >= 10)
        {
            return $"{seconds:0.00} s";
        }

        return $"{seconds:0.000} s";
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        skView?.ResetView();
    }

    // 跳转至末端按钮事件处理（平滑滚动并显示进度）
    private void OnJumpToEndClicked(object? sender, RoutedEventArgs e)
    {
        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        if (skView is null) return;
        if (DataContext is not ViewModels.TdmsViewerViewModel vm) return;

        // 绑定进度事件，仅绑定一次以避免重复累加
        skView.JumpingStateChanged -= OnJumpingStateChanged;
        skView.JumpProgressChanged -= OnJumpProgressChanged;
        skView.JumpingStateChanged += OnJumpingStateChanged;
        skView.JumpProgressChanged += OnJumpProgressChanged;

        vm.IsJumping = true;
        vm.JumpProgress = 0;
        skView.JumpToEndSmooth(TimeSpan.FromMilliseconds(600));

        void OnJumpingStateChanged(bool running)
        {
            vm.IsJumping = running;
            if (!running) vm.JumpProgress = 100;
        }
        void OnJumpProgressChanged(double p)
        {
            vm.JumpProgress = (int)Math.Clamp(Math.Round(p), 0, 100);
        }
    }

    // 文件选择按钮事件处理
    private async void OnPickFileClicked(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog()
        {
            Title = "选择 TDMS/TDM 文件",
            AllowMultiple = false,
            Filters = new System.Collections.Generic.List<FileDialogFilter>
            {
                new FileDialogFilter { Name = "TDMS/TDM 文件", Extensions = new System.Collections.Generic.List<string> { "tdms", "tdm" } },
                new FileDialogFilter { Name = "所有文件", Extensions = new System.Collections.Generic.List<string> { "*" } }
            }
        };
        try
        {
            var initialDir = TryGetDataDir();
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                dlg.Directory = initialDir;
        }
        catch { }

        var win = this.VisualRoot as Window;
        if (win is null) return;

        var result = await dlg.ShowAsync(win);
        var fp = result?.Length > 0 ? result[0] : null;
        if (!string.IsNullOrWhiteSpace(fp) && File.Exists(fp))
        {
            SelectedFile = fp; // 触发属性转发到 ViewModel
        }
    }

    // 尝试解析 data 目录（优先工作区路径）
    private static string? TryGetDataDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Directory.GetCurrentDirectory(), "data"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data")),
            "D:/DH2/data",
            "D\\DH2\\data"
        };
        foreach (var p in candidates)
        {
            try
            {
                if (Directory.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
