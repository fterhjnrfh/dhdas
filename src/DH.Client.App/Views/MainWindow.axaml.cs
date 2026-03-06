using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DH.Client.App.Controls;
using DH.Client.App.Data;
using DH.Client.App.Views;
using DH.Configmanage.MockConfig;
// 暂时移除 ScottPlot 5.x 依赖以兼容 .NET 6.0
// using DH.Display.Realtime;
// using DH.Display.Skia.Realtime;

namespace DH.Client.App.Views;

public partial class MainWindow : Window
{



    private DispatcherTimer? _timer;
    private MockConfig? _cfg;
    // private GridHub? _hub;
    // UI曲线面板的数据总线（将 IDataBus 的帧桥接为 CurvePoint）

    private DataHub? _dataHub;
    private CancellationTokenSource? _bridgeCts;
    private Task? _bridgeTask;
    // 多视图容器及面板集合
    private UniformGrid? _viewsContainer;
    private Button? _addViewButton;
    private Button? _removeViewButton;
    private StackPanel? _resultsControlsPanel;
    private Button? _globalChannelSelectorButton;
    private ChannelSelector? _globalChannelSelector;
    // 视图选择机制
    private CurvePanel? _selectedPanel;
    private int _selectedIndex = -1;
    private readonly List<CurvePanel> _curvePanels = new();

    /// <summary>
    /// 根据视图数量计算最优的网格布局（行×列）
    /// 优先选择正方形布局，如8×8, 7×7, 6×6等
    /// 对于非完全平方数，选择最接近的矩形布局
    /// </summary>
    /// <param name="viewCount">视图数量</param>
    /// <returns>元组(行数, 列数)</returns>
    private static (int rows, int cols) CalculateOptimalGrid(int viewCount)
    {
        if (viewCount <= 0) return (1, 1);
        if (viewCount == 1) return (1, 1);
        
        // 计算平方根，优先选择正方形布局
        var sqrt = (int)Math.Ceiling(Math.Sqrt(viewCount));
        
        // 检查是否为完全平方数
        if (sqrt * sqrt == viewCount)
        {
            return (sqrt, sqrt);
        }
        
        // 对于非完全平方数，寻找最接近的矩形布局
        // 尝试从sqrt开始向下寻找合适的行数
        for (int rows = sqrt; rows >= 1; rows--)
        {
            int cols = (int)Math.Ceiling((double)viewCount / rows);
            if (rows * cols >= viewCount && Math.Abs(rows - cols) <= 1)
            {
                return (rows, cols);
            }
        }
        
        // 如果没有找到理想的布局，使用默认计算
        int defaultRows = sqrt;
        int defaultCols = (int)Math.Ceiling((double)viewCount / defaultRows);
        return (defaultRows, defaultCols);
    }

    /// <summary>
    /// 更新UniformGrid的行列布局
    /// </summary>
    private void UpdateGridLayout()
    {
        if (_viewsContainer is null) return;
        
        var (rows, cols) = CalculateOptimalGrid(_curvePanels.Count);
        _viewsContainer.Rows = rows;
        _viewsContainer.Columns = cols;
        
        Console.WriteLine($"Updated grid layout: {rows}×{cols} for {_curvePanels.Count} views");
    }

    public MainWindow()
    {
        InitializeComponent();
        Console.WriteLine("MainWindow constructor completed");

        this.Opened += OnOpenedMultiGrid;
        this.Closing += async (_, __) =>
        {
            // 清理ViewModel资源
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.Cleanup();
            }
            
            _timer?.Stop();
            
            
            
            // 停止所有 CurvePanel 与桥接
            foreach (var panel in _curvePanels)
            {
                panel.Stop();
            }
            var cts = _bridgeCts; _bridgeCts = null;
            if (cts != null) cts.Cancel();
            var task = _bridgeTask; _bridgeTask = null;
            if (task != null) { try { await task.ConfigureAwait(false); } catch { }
            }
        };

        _cfg = MockConfig.Instance;
    }

    private void OnOpenedMultiGrid(object? s, EventArgs e)
    {

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;
    
        // 直接使用真实数据总线，避免离线时显示模拟数据
        _dataHub = new DataHub(_bus);
    
        // 连接前不生成/桥接任何数据；连接成功后由 _bus 推送真实帧
    
        // 找到多视图容器与按钮
        _viewsContainer = this.FindControl<UniformGrid>("ViewsContainer");
        _addViewButton = this.FindControl<Button>("AddViewButton");
        _removeViewButton = this.FindControl<Button>("RemoveViewButton");
        _resultsControlsPanel = this.FindControl<StackPanel>("ResultsControlsPanel");
        _globalChannelSelectorButton = this.FindControl<Button>("GlobalChannelSelectorButton");
        _globalChannelSelector = this.FindControl<ChannelSelector>("GlobalChannelSelector");
    
        if (_addViewButton is not null)
            _addViewButton.Click += (_, __) => AddView();
        if (_removeViewButton is not null)
            _removeViewButton.Click += (_, __) => RemoveView();

        // 设置全局通道选择器的数据源，并将选择应用到当前激活视图
        if (_globalChannelSelector is not null && vm is not null)
        {
            _globalChannelSelector.SetOnlineChannelManager(vm.OnlineChannelManager);
            if (_dataHub is not null)
            {
                _globalChannelSelector.AttachDataBus(_dataHub.DataBus);
            }
            _globalChannelSelector.SelectedChannelsChanged += (s2, e2) =>
            {
                var target = TargetPanel();
                target?.SetSelectedChannels(e2.SelectedChannels);
            };

            vm.PropertyChanged += (s2, e2) =>
            {
                if (e2.PropertyName == "SelectedDeviceId")
                {
                    var target = TargetPanel();
                    target?.SetDeviceFilter(vm.SelectedDeviceId);
                }
            };
        }

        // 顶部控件组宽度限制为画布宽度的30%，并保持与曲线区域至少20像素间距
        this.LayoutUpdated += (_, __) =>
        {
            if (_resultsControlsPanel is not null && _viewsContainer is not null)
            {
                double w = _viewsContainer.Bounds.Width;
                _resultsControlsPanel.MaxWidth = Math.Max(100, w * 0.3);
            }
        };
    
        // 初始填充：从1个视图开始，支持动态调整
        AddView();
    
        // 统一刷新策略（16ms），后续可由 DataHub 接管
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, __) =>
        {
            foreach (var p in _curvePanels)
                p.Invalidate();
        };
        _timer.Start();
    
        
        
        // 订阅采样频率变更事件
        vm.SampleRateChanged += OnSampleRateChanged;
    }

    private void AddView()
    {
        if (_viewsContainer is null || _dataHub is null) return;
        // 限制最多视图数量以避免过载（可按需调整）
        const int MaxViews = 64;
        if (_curvePanels.Count >= MaxViews) return;

        var panel = new CurvePanel
        {
            Margin = new Thickness(4)
        };
        panel.AttachDataHub(_dataHub);
        
        // 设置在线通道管理器
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            panel.SetOnlineChannelManager(vm.OnlineChannelManager);
            panel.SetDeviceFilter(vm.SelectedDeviceId);
            panel.UpdateSampleRate(vm.SampleRate);
        }
        
        _curvePanels.Add(panel);
        _viewsContainer.Children.Add(panel);

        // 视图点击选择：点击后设为当前选中，并高亮
        panel.PointerPressed += (_, __) => SelectPanel(panel);

        // 新增视图默认设为选中，便于立即联动
        SelectPanel(panel);

        // 动态更新网格布局
        UpdateGridLayout();
    }

    // 顶部全局控件事件：仅作用于当前选中视图（无选中则默认最后一个）
    private CurvePanel? TargetPanel() => _selectedPanel ?? (_curvePanels.Count > 0 ? _curvePanels[^1] : null);

    private void OnGlobalZoomInX(object? sender, RoutedEventArgs e) => TargetPanel()?.ZoomInX();
    private void OnGlobalZoomOutX(object? sender, RoutedEventArgs e) => TargetPanel()?.ZoomOutX();
    private void OnGlobalZoomInY(object? sender, RoutedEventArgs e) => TargetPanel()?.ZoomInY();
    private void OnGlobalZoomOutY(object? sender, RoutedEventArgs e) => TargetPanel()?.ZoomOutY();
    private void OnGlobalResetZoom(object? sender, RoutedEventArgs e) => TargetPanel()?.ResetZoom();

    private void OnSampleRateValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel && e.NewValue.HasValue)
        {
            viewModel.SampleRateChangedCommand.Execute(e.NewValue.Value);
        }
    }

    private void OnSampleRateSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRateChangedCommand.Execute((int)e.NewValue);
        }
    }

    private void OnQuickSampleRate100(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 100;
            viewModel.SampleRateChangedCommand.Execute(100);
        }
    }

    private void OnQuickSampleRate1k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 1000;
            viewModel.SampleRateChangedCommand.Execute(1000);
        }
    }

    private void OnQuickSampleRate5k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 5000;
            viewModel.SampleRateChangedCommand.Execute(5000);
        }
    }

    private void OnQuickSampleRate10k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 10000;
            viewModel.SampleRateChangedCommand.Execute(10000);
        }
    }

    private void RemoveView()
    {
        if (_viewsContainer is null) return;
        if (_curvePanels.Count == 0) return;

        var panel = _curvePanels[^1];
        panel.Stop();
        _curvePanels.RemoveAt(_curvePanels.Count - 1);
        _viewsContainer.Children.Remove(panel);
        
        // 若删除的是当前选中视图，重置选中到最后一个
        if (_selectedPanel == panel)
        {
            _selectedPanel = _curvePanels.Count > 0 ? _curvePanels[^1] : null;
            _selectedIndex = _selectedPanel != null ? _curvePanels.IndexOf(_selectedPanel) : -1;
            foreach (var p in _curvePanels) p.SetSelected(p == _selectedPanel);
            SyncControlsToSelectedView();
        }
        
        // 动态更新网格布局
        UpdateGridLayout();
    }

    private void OnChannelOnlineChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is int channelId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.OnlineChannelManager.SetChannelOnline(channelId, true);
            }
        }
    }

    // ===== 视图选择与联动 =====
    private void SelectPanel(CurvePanel panel)
    {
        _selectedPanel = panel;
        _selectedIndex = _curvePanels.IndexOf(panel);
        // 高亮选中视图
        foreach (var p in _curvePanels)
            p.SetSelected(p == panel);
        // 同步上方控件状态到当前选中视图
        SyncControlsToSelectedView();
        Console.WriteLine($"Selected view index: {_selectedIndex}");
    }

    private void SyncControlsToSelectedView()
    {
        if (_globalChannelSelector is null) return;
        var sel = _selectedPanel;
        if (sel is null)
        {
            _globalChannelSelector.SetSelectedChannels(Array.Empty<int>());
            return;
        }
        var chs = sel.GetSelectedChannels();
        _globalChannelSelector.SetSelectedChannels(chs);
    }

    private void OnChannelOnlineUnchecked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is int channelId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.OnlineChannelManager.SetChannelOnline(channelId, false);
            }
        }
    }

    private void OnDeviceTilePointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Border border && border.Tag is int deviceId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.SelectedDeviceId = deviceId;
            }
        }
    }
    
    // 采样频率变更事件处理
    private void OnSampleRateChanged(object? sender, int newSampleRate)
    {
        // 更新所有曲线面板的采样频率
        foreach (var panel in _curvePanels)
        {
            panel.UpdateSampleRate(newSampleRate);
        }
    }



#if false
    private void OnOpenedHistorySkia(object? s, EventArgs e)
    {

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;

        // 启动历史写入
        _hist = new HistoryWorker(_bus, channelId: 1, _cfg.SampleRate, historySeconds: 600);
        _hist.Start();

        // 附着到控件
        var plot = this.FindControl<SkiaHistoryPlotControl>("Plot");
        plot.SampleRate = _cfg.SampleRate;
        plot.YMin = -1.2; plot.YMax = 1.2;
        plot.AttachWorker(_hist, initialSeconds: 5.0);

        // 30 FPS 刷新 + 实时跟随
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, __) => plot.LiveFollowAndInvalidate();
        _timer.Start();
    }

    private void OnGoLiveClicked(object? sender, RoutedEventArgs e)
    {
        var plot = this.FindControl<SkiaHistoryPlotControl>("Plot");
        plot?.GoLive();
    }



    private void OnOpenedSkia(object? sender, EventArgs e)
    {
        // 暂时禁用 ScottPlot 5.x 功能以兼容 .NET 6.0
        /*
        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;

        // 启动 Skia 后台渲染器（独立线程订阅并绘制到离屏位图）
        _worker = new SkiaRealtimePlotWorker(_bus, _channelId, width: 900, height: 360);
        _worker.YMin = -1.2; _worker.YMax = 1.2;
        _worker.Start();

        // 将 worker 附着到控件；UI 仅负责贴图与画轴，Plot为MainWindow.axaml中 SkiaPlotControl的别名
        Plot.AttachWorker(_worker);
        Plot.XSeconds = 5.0;  // 仅刻度显示
        Plot.YMin = -1.2; Plot.YMax = 1.2;

        // UI 定时刷新（30 FPS），不参与绘制运算
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, __) => Plot.InvalidateVisual();
        _timer.Start();
        */
    }


    private void OnOpenedEcg(object? sender, EventArgs e)
    {
        // 暂时禁用 ScottPlot 5.x 功能以兼容 .NET 6.0
        /*
        var avaPlot = this.FindControl<ScottPlot.Avalonia.AvaPlot>("EcgPlot");
        if (avaPlot is null) return;

        var host = new AvaloniaPlotHost(avaPlot);

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus;
        var _channelId = vm.ChannelId;

        _renderer = new EcgSignalRenderer(_bus, _channelId, MockConfig.Instance);
        _renderer.AttachHost(host);
        _renderer.Start();
        */
    }
} 
#endif
}