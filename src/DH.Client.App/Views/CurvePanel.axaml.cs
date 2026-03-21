using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using DH.Contracts.Abstractions;
using DH.Client.App.Data;
using System.Collections.Generic;
using System.Linq;
using DH.Client.App.Controls;

namespace DH.Client.App.Views
{
    public partial class CurvePanel : UserControl
    {
        private SkiaMultiChannelView _skView;
        private Border? _openGLContainerRef;
        private ChannelSelector? _channelSelector;
        private ChannelSelector? _flyoutChannelSelector;
        private Button? _toggleChannelSelectorButton;
        private Button? _zoomInXButton;
        private Button? _zoomOutXButton;
        private Button? _zoomInYButton;
        private Button? _zoomOutYButton;
        private Button? _resetZoomButton;
        private TextBlock? _selectedChannelsText;
        private DataBus? _dataBus; // 数据总线
        private DataHub? _dataHub; // 数据中心
        private readonly DispatcherTimer _renderTimer; // 渲染计时器
        private OnlineChannelManager? _onlineChannelManager; // 在线通道管理器
        private List<int> _selectedChannelIds = new();
        private float _zoomLevelX = 1.0f;  // 横轴缩放
    private float _zoomLevelY = 2.0f;  // 纵轴缩放，调整为适合50-100振幅范围，显示为100-200像素
        private bool _useExternalRefresh = true; // 统一刷新策略由外部管理（DataHub/主窗口定时器）
        private bool _autoFitX = true;   // 自动适配X轴以完整显示曲线
        private bool _autoFitY = true;   // 自动适配Y轴以完整显示曲线
        public bool IsSelected { get; private set; } = false; // 视图选中状态
        private int _deviceFilterId = 0; // 当前设备过滤（0=不限制）
        private bool _disableAutoSelection = false; // 禁用自动选择通道（用户主动清空时）

        private const double SweepWindowSeconds = 10.0;
        private const int SweepHistoryPointBudget = 8192;
        private const int TotalPreviewPointBudget = 65536;
        private const int MinPreviewPointsPerChannel = 128;
        private const int MaxPreviewPointsPerChannel = 4096;
        private double _sweepWindowStartSeconds;

        public CurvePanel()
        {
            InitializeComponent();
            
            // 获取UI元素引用
            _channelSelector = this.FindControl<ChannelSelector>("ChannelSelector");
            _flyoutChannelSelector = this.FindControl<ChannelSelector>("FlyoutChannelSelector");
            _toggleChannelSelectorButton = this.FindControl<Button>("ToggleChannelSelectorButton");
            _zoomInXButton = this.FindControl<Button>("ZoomInXButton");
            _zoomOutXButton = this.FindControl<Button>("ZoomOutXButton");
            _zoomInYButton = this.FindControl<Button>("ZoomInYButton");
            _zoomOutYButton = this.FindControl<Button>("ZoomOutYButton");
            _resetZoomButton = this.FindControl<Button>("ResetZoomButton");
            _selectedChannelsText = this.FindControl<TextBlock>("SelectedChannelsText");
            
            // 创建 OpenGL 曲线视图（专门用于曲线绘制）
            _skView = new SkiaMultiChannelView
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                // 时间轴滚动模式
                UseTimeAxis = false,
                UseDataXValues = true,
                UseFixedDataXRange = true,
                FixedDataXMin = 0.0,
                FixedDataXMax = SweepWindowSeconds,
                SampleRateHz = 100,  // 采样率 100 Hz（可根据实际调整）
                TimeWindowSeconds = 20.0, // 固定显示 20 秒
                ScrollMode = false,
                ScrollWindowSize = 2000, // 不使用
                UseOscilloscopeMode = false, // 不使用示波器模式
                ReverseXRendering = false // 禁用 X 轴反转
            };

            // 横轴刻度与标签格式：显示整数秒（如 0s、5s、10s、15s、20s）
            _skView.DesiredXTicks = 5; // 20 秒窗口 → 5 个主刻度 → 步长约 5s
            _skView.ShowAbsoluteTime = false;
            _skView.FormatXLabel = sec =>
            {
                if (sec < 1.0) return $"{sec*1000:0} ms";
                return $"{sec:0} s";
            };
            _skView.TimeWindowSeconds = SweepWindowSeconds;
            _skView.FormatXLabel = FormatSweepAxisLabel;
            
            // 添加到容器并保存引用（用于选中高亮）
            _openGLContainerRef = this.FindControl<Border>("OpenGLContainer");
            _openGLContainerRef.Child = _skView;
            
            // 设置事件处理
            if (_zoomInXButton != null) _zoomInXButton.Click += OnZoomInXClick;
            if (_zoomOutXButton != null) _zoomOutXButton.Click += OnZoomOutXClick;
            if (_zoomInYButton != null) _zoomInYButton.Click += OnZoomInYClick;
            if (_zoomOutYButton != null) _zoomOutYButton.Click += OnZoomOutYClick;
            if (_resetZoomButton != null) _resetZoomButton.Click += OnResetZoomClick;
            if (_channelSelector != null) _channelSelector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            if (_flyoutChannelSelector != null)
                _flyoutChannelSelector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            
            // Flyout 打开时确保其内部 ChannelSelector 已正确接线
            var flyout = _toggleChannelSelectorButton?.Flyout as Flyout;
            if (flyout != null)
            {
                flyout.Opened += (s, e) => EnsureFlyoutSelectorWired();
            }
            
            // 初始化缩放函数
            UpdateZoomFunctions();
            
            // 设置渲染计时器 (60 FPS)
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16.67) // ~60 FPS
            };
            _renderTimer.Tick += (s, e) => _skView.InvalidateVisual();
            
            // 默认：不强制修改选中状态，等待管理器设置后由ChannelSelector默认勾选在线通道
            Dispatcher.UIThread.Post(() =>
            {
                UpdateSelectedChannelsDisplay();
                _skView?.InvalidateVisual();
            }, DispatcherPriority.Loaded);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // 在线通道变化事件处理：实时刷新界面显示
        private void OnOnlineChannelsChanged(object? sender, OnlineChannelsChangedEventArgs e)
        {
            // 事件可能来自后台线程，调度到UI线程刷新
            Dispatcher.UIThread.Post(() =>
            {
                UpdateSelectedChannelsDisplay();
                _skView?.InvalidateVisual();
            });
        }
        
        
        // 横轴放大按钮点击事件
        private void OnZoomInXClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = false; // 手动缩放后关闭X轴自适配
            _zoomLevelX *= 1.2f; // 放大
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
            Console.WriteLine($"横轴放大: {_zoomLevelX:F2}");
        }
        
        // 横轴缩小按钮点击事件
        private void OnZoomOutXClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = false;
            _zoomLevelX /= 1.2f; // 缩小
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
            Console.WriteLine($"横轴缩小: {_zoomLevelX:F2}");
        }
        
        // 纵轴放大按钮点击事件
        private void OnZoomInYClick(object? sender, RoutedEventArgs e)
        {
            _autoFitY = false; // 手动缩放后关闭Y轴自适配
            _zoomLevelY *= 1.2f; // 放大
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
            Console.WriteLine($"纵轴放大: {_zoomLevelY:F2}");
        }
        
        // 纵轴缩小按钮点击事件
        private void OnZoomOutYClick(object? sender, RoutedEventArgs e)
        {
            _autoFitY = false;
            _zoomLevelY /= 1.2f; // 缩小
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
            Console.WriteLine($"纵轴缩小: {_zoomLevelY:F2}");
        }
        
        // 重置缩放按钮点击事件
        private void OnResetZoomClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = true; // 重置到自适配
            _autoFitY = true;
            _zoomLevelX = 1.0f;
            _zoomLevelY = 1.0f; // 重置为1，由自适配决定最终显示比例
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
            Console.WriteLine($"重置缩放并启用自适配: X={_zoomLevelX:F2}, Y={_zoomLevelY:F2}");
        }
        
        // 更新缩放函数
        private void UpdateZoomFunctions()
        {
            _skView.GetZoomX = () => _zoomLevelX;
            _skView.GetZoomY = () => _zoomLevelY;
            _skView.IsAutoFitX = () => _autoFitX;
            _skView.IsAutoFitY = () => _autoFitY;
        }
        
        // 切换通道选择器显示/隐藏
        // 通道选择变更事件
        private void OnSelectedChannelsChanged(object? sender, SelectedChannelsChangedEventArgs e)
        {
            _selectedChannelIds = e.SelectedChannels;
            UpdateSelectedChannelsDisplay();
            UpdateDataSubscriptions();

            // 当选择被清空时，清空本视图显示并强制刷新
            // 保留数据总线缓冲区，避免影响其他视图
            if (_selectedChannelIds.Count == 0)
            {
                // 清空选择时，确保视图完全清除，并禁用自动选择
                _skView?.ResetView();
                _disableAutoSelection = true; // 标记为用户主动清空，禁用自动选择
            }
            else
            {
                _disableAutoSelection = false; // 有选择时重新启用自动选择
            }
            _skView?.InvalidateVisual();
        }
        
        private void EnsureFlyoutSelectorWired()
        {
            var flyout = _toggleChannelSelectorButton?.Flyout as Flyout;
            if (flyout?.Content is ChannelSelector selector)
            {
                if (_onlineChannelManager != null)
                {
                    selector.SetOnlineChannelManager(_onlineChannelManager);
                }
                // 先取消再订阅，避免重复绑定
                selector.SelectedChannelsChanged -= OnSelectedChannelsChanged;
                selector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            }
        }
        
        // 附加数据总线和数据中心
        public void AttachDataHub(DataHub dataHub)
        {
            _dataHub = dataHub;
            _dataBus = dataHub.DataBus;
        
            // 设置数据提供者（支持多通道）
            _skView.DataProvider = GetMultiChannelData; // 保持向后兼容
            _skView.MultiChannelDataProvider = GetAllChannelData; // 新的多通道数据提供者
            
            // 启动渲染计时器（如使用外部统一刷新，则无需内部计时器）
            if (!_useExternalRefresh)
                _renderTimer.Start();
        
            if (_channelSelector != null && _dataBus != null)
            {
                _channelSelector.AttachDataBus(_dataBus);
            }
            if (_flyoutChannelSelector != null && _dataBus != null)
            {
                _flyoutChannelSelector.AttachDataBus(_dataBus);
            }

            if (_dataBus != null)
            {
                _dataBus.ChannelAdded += (_, __) => EnsureDefaultChannelSelection();
            }
        }

        // 设置在线通道管理器
        public void SetOnlineChannelManager(OnlineChannelManager onlineChannelManager)
        {
            _onlineChannelManager = onlineChannelManager;
            
            // 将在线通道管理器传递给ChannelSelector
            if (_channelSelector != null)
            {
                _channelSelector.SetOnlineChannelManager(onlineChannelManager);
            }
            if (_flyoutChannelSelector != null)
            {
                _flyoutChannelSelector.SetOnlineChannelManager(onlineChannelManager);
            }
            
            // 订阅在线通道变化，实时响应界面显示
            if (_onlineChannelManager != null)
            {
                _onlineChannelManager.OnlineChannelsChanged += OnOnlineChannelsChanged;
            }
        }

        // 设置当前视图显示的通道集合
        public void SetChannels(int[] channelIds)
        {
            if (channelIds == null || channelIds.Length == 0)
                return;
                
            _selectedChannelIds = channelIds.ToList();
            _channelSelector?.SetSelectedChannels(channelIds);
            UpdateSelectedChannelsDisplay();
            UpdateDataSubscriptions();
        }

        // 设置设备过滤（仅自动选择该设备的通道）
        public void SetDeviceFilter(int deviceId)
        {
            _deviceFilterId = Math.Clamp(deviceId, 0, 64);
            EnsureDefaultChannelSelection();
        }

        // 若未选择通道，自动选择第一个可用通道
        private void EnsureDefaultChannelSelection()
        {
            if (_dataBus == null) return;
            if (_disableAutoSelection) return; // 用户主动清空后禁用自动选择
            // 新方案：到底不自动选择，二级是 ChannelSelector 全选
            // 此处有选择时直接返回
            if (_selectedChannelIds.Count > 0) return;
            var available = _dataBus.GetAvailableChannels();
            if (available.Count == 0) return;
            var filtered = _deviceFilterId > 0
                ? available.Where(id => id / 100 == _deviceFilterId).ToList()
                : available.ToList();
            // 不再自动选择，由 ChannelSelector 控制全选
            _skView?.InvalidateVisual();
        }

        // 放大/缩小交互的公共方法（同时缩放X和Y轴）
        public void ZoomIn() 
        {
            OnZoomInXClick(this, new RoutedEventArgs());
            OnZoomInYClick(this, new RoutedEventArgs());
        }
        
        public void ZoomOut() 
        {
            OnZoomOutXClick(this, new RoutedEventArgs());
            OnZoomOutYClick(this, new RoutedEventArgs());
        }

        // 请求重绘（由外部统一定时器调用）
        public void Invalidate()
        {
            _skView?.InvalidateVisual();
        }

        // 释放资源
        public void Dispose()
        {
            Stop();
        }
        
        // 获取多通道数据（合并显示）
        private int GetPreviewSampleCount(int activeChannelCount)
        {
            double width = _skView?.Bounds.Width > 0 ? _skView.Bounds.Width : Bounds.Width;
            if (width <= 0)
            {
                width = 900;
            }

            int widthBudget = Math.Max(MinPreviewPointsPerChannel, (int)Math.Ceiling(width * 2.0));
            int perChannelBudget = Math.Max(MinPreviewPointsPerChannel, TotalPreviewPointBudget / Math.Max(1, activeChannelCount));
            return Math.Clamp(Math.Min(widthBudget, perChannelBudget), MinPreviewPointsPerChannel, MaxPreviewPointsPerChannel);
        }

        private int GetSweepHistoryPointCount(int activeChannelCount)
        {
            return Math.Max(GetPreviewSampleCount(activeChannelCount), SweepHistoryPointBudget);
        }

        private List<int> GetRenderableChannels()
        {
            if (_selectedChannelIds.Count == 0)
            {
                return new List<int>();
            }

            return _selectedChannelIds
                .Where(id => _onlineChannelManager?.IsChannelOnline(id) ?? true)
                .ToList();
        }

        private static double GetSweepWindowStart(double latestSeconds)
        {
            if (latestSeconds <= 0.0)
            {
                return 0.0;
            }

            return Math.Floor(latestSeconds / SweepWindowSeconds) * SweepWindowSeconds;
        }

        private string FormatSweepAxisLabel(double valueSeconds)
        {
            double absoluteSeconds = _sweepWindowStartSeconds + valueSeconds;
            if (Math.Abs(absoluteSeconds) < 0.0005)
            {
                absoluteSeconds = 0.0;
            }

            return $"{absoluteSeconds:0.#} s";
        }

        private static int FindFirstPointAtOrAfter(IReadOnlyList<DH.Contracts.Models.CurvePoint> data, double targetSeconds)
        {
            int low = 0;
            int high = data.Count;
            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                if (data[mid].X < targetSeconds)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private IReadOnlyList<DH.Contracts.Models.CurvePoint> SliceSweepWindow(
            IReadOnlyList<DH.Contracts.Models.CurvePoint> source,
            double windowStartSeconds,
            double windowEndSeconds)
        {
            if (source == null || source.Count == 0)
            {
                return Array.Empty<DH.Contracts.Models.CurvePoint>();
            }

            int startIndex = FindFirstPointAtOrAfter(source, windowStartSeconds);
            if (startIndex >= source.Count)
            {
                return Array.Empty<DH.Contracts.Models.CurvePoint>();
            }

            int endIndex = FindFirstPointAtOrAfter(source, windowEndSeconds);
            int count = endIndex - startIndex;
            if (count <= 0)
            {
                return Array.Empty<DH.Contracts.Models.CurvePoint>();
            }

            if (windowStartSeconds <= 0.0 && startIndex == 0 && endIndex == source.Count)
            {
                return source;
            }

            var projected = new DH.Contracts.Models.CurvePoint[count];
            for (int i = 0; i < count; i++)
            {
                var point = source[startIndex + i];
                projected[i] = new DH.Contracts.Models.CurvePoint(point.X - windowStartSeconds, point.Y);
            }

            return projected;
        }

        private Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> BuildSweepChannelData(IReadOnlyList<int> channels)
        {
            var result = new Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>>(channels.Count);
            if (_dataBus == null || channels.Count == 0)
            {
                _sweepWindowStartSeconds = 0.0;
                return result;
            }

            int historyCount = GetSweepHistoryPointCount(channels.Count);
            double latestSeconds = double.NegativeInfinity;

            foreach (int channelId in channels)
            {
                var channelData = _dataBus.GetLatestData(channelId, historyCount);
                result[channelId] = channelData;
                if (channelData.Count > 0)
                {
                    latestSeconds = Math.Max(latestSeconds, channelData[channelData.Count - 1].X);
                }
            }

            if (double.IsNegativeInfinity(latestSeconds))
            {
                _sweepWindowStartSeconds = 0.0;
                return result;
            }

            double windowStartSeconds = GetSweepWindowStart(latestSeconds);
            double windowEndSeconds = windowStartSeconds + SweepWindowSeconds;
            _sweepWindowStartSeconds = windowStartSeconds;
            _skView.FixedDataXMin = 0.0;
            _skView.FixedDataXMax = SweepWindowSeconds;

            foreach (int channelId in channels)
            {
                result[channelId] = SliceSweepWindow(result[channelId], windowStartSeconds, windowEndSeconds);
            }

            return result;
        }

        private IReadOnlyList<DH.Contracts.Models.CurvePoint> GetMultiChannelData()
        {
            if (_dataBus == null || !_selectedChannelIds.Any())
                return Array.Empty<DH.Contracts.Models.CurvePoint>();

            // 简单实现：取第一个选中通道的数据
            // 更复杂的实现可以合并多个通道的数据
            var channels = GetRenderableChannels();
            if (channels.Count == 0)
            {
                return Array.Empty<DH.Contracts.Models.CurvePoint>();
            }

            int primaryChannelId = channels[0];
            var windowData = BuildSweepChannelData(channels);
            
            // 更新SkiaMultiChannelView的起始时间戳，实现时间轴实时更新
            return windowData.TryGetValue(primaryChannelId, out var data)
                ? data
                : Array.Empty<DH.Contracts.Models.CurvePoint>();
        }

        // 获取所有选中通道的数据字典
        private Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> GetAllChannelData()
        {
            var result = new Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>>();
            
            if (_dataBus == null)
                return result;

            // 规则调整：
            // - 若未选择通道，则不显示任何曲线
            // - 若已选择通道，则仅显示“选中且在线”的通道
            if (_selectedChannelIds.Count == 0)
            {
                return result;
            }

            var channels = GetRenderableChannels();
            if (channels.Count == 0)
            {
                _sweepWindowStartSeconds = 0.0;
                return result;
            }

            return BuildSweepChannelData(channels);
        }

        /*
                
                // 从数据中提取最新时间戳，用于设置StartTimestampUtc
            }

            // 更新SkiaMultiChannelView的起始时间戳，实现时间轴实时更新
        }

        // 获取单个通道的数据
        */
        private IReadOnlyList<DH.Contracts.Models.CurvePoint> GetChannelData(int channelId)
        {
            if (_dataBus == null)
                return Array.Empty<DH.Contracts.Models.CurvePoint>();

            var channels = new List<int> { channelId };
            var windowData = BuildSweepChannelData(channels);
            return windowData.TryGetValue(channelId, out var data)
                ? data
                : Array.Empty<DH.Contracts.Models.CurvePoint>();
        }

        // 更新选中通道显示文本
        private void UpdateSelectedChannelsDisplay()
        {
            if (_selectedChannelsText == null)
                return;
            
            if (!_selectedChannelIds.Any())
            {
                _selectedChannelsText.Text = "未选择通道";
            }
            else if (_selectedChannelIds.Count == 1)
            {
                _selectedChannelsText.Text = $"通道 {_selectedChannelIds[0]}";
            }
            else
            {
                _selectedChannelsText.Text = $"已选择 {_selectedChannelIds.Count} 个通道";
            }
        }

        // 更新数据订阅
        private void UpdateDataSubscriptions()
        {
            if (_dataHub == null)
                return;

            // 这里可以实现订阅逻辑
            // 当前简化实现，实际应该订阅选中的通道
            foreach (var channelId in _selectedChannelIds)
            {
                // _dataHub.Subscribe(channelId, OnChannelDataReceived);
            }
        }
        
        // 停止渲染
        public void Stop()
        {
            _renderTimer?.Stop();
        }

        // ===== 公共方法：供主窗口顶栏控件调用 =====
        public void ZoomInX()
        {
            _autoFitX = false;
            _zoomLevelX *= 1.2f;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void ZoomOutX()
        {
            _autoFitX = false;
            _zoomLevelX /= 1.2f;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void ZoomInY()
        {
            _autoFitY = false;
            _zoomLevelY *= 1.2f;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void ZoomOutY()
        {
            _autoFitY = false;
            _zoomLevelY /= 1.2f;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void ResetZoom()
        {
            _autoFitX = true;
            _autoFitY = true;
            _zoomLevelX = 1.0f;
            _zoomLevelY = 1.0f;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void SetSelectedChannels(List<int> channelIds)
        {
            _selectedChannelIds = channelIds ?? new List<int>();
            UpdateSelectedChannelsDisplay();
            UpdateDataSubscriptions();
            
            // 当通道列表为空时，清除视图状态
            if (_selectedChannelIds.Count == 0)
            {
                _skView?.ResetView();
                _disableAutoSelection = true; // 禁用自动选择
            }
            else
            {
                _disableAutoSelection = false; // 重新启用自动选择
            }
            _skView?.InvalidateVisual();
        }

        // ===== 选中与高亮 =====
        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (_openGLContainerRef != null)
            {
                _openGLContainerRef.BorderBrush = new SolidColorBrush(Color.Parse(selected ? "#409EFF" : "#2B2B2B"));
                _openGLContainerRef.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            }
        }

        public int[] GetSelectedChannels()
        {
            return _selectedChannelIds.ToArray();
        }
        
        // 更新采样频率
        public void UpdateSampleRate(int sampleRate)
        {
            if (_skView != null)
            {
                _skView.SampleRateHz = sampleRate;
                _autoFitX = true;
                _zoomLevelX = 1.0f;
                UpdateZoomFunctions();
                _skView.UseDataXValues = true;
                _skView.UseFixedDataXRange = true;
                _skView.FixedDataXMin = 0.0;
                _skView.FixedDataXMax = SweepWindowSeconds;
                _skView.TimeWindowSeconds = SweepWindowSeconds;
                _skView.UseTimeAxis = true; // 启用时间轴
                _skView.InvalidateVisual();
                _skView.UseTimeAxis = false;
                _skView.ShowAbsoluteTime = false;
                _skView.ResetView();
                _skView.InvalidateVisual();
            }
        }
    }

    // 简易绘制控件：使用 Avalonia DrawingContext 绘制折线
    internal class CurveCanvas : Control
    {
        public Func<IReadOnlyList<DH.Contracts.Models.CurvePoint>> DataProvider { get; set; } = () => Array.Empty<DH.Contracts.Models.CurvePoint>();
        public Func<Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>>> MultiChannelDataProvider { get; set; } = () => new Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>>();
        public Func<float> GetZoomX { get; set; } = () => 1.0f;  // 横轴缩放
        public Func<float> GetZoomY { get; set; } = () => 1.0f;  // 纵轴缩放
        public Func<bool> IsAutoFitX { get; set; } = () => true; // 是否自适配X轴
        public Func<bool> IsAutoFitY { get; set; } = () => true; // 是否自适配Y轴

        // 预定义颜色调色板
        private static readonly Color[] ColorPalette = new Color[]
        {
            Color.Parse("#3DDC84"), // 绿色
            Color.Parse("#FF6B6B"), // 红色
            Color.Parse("#4ECDC4"), // 青色
            Color.Parse("#FFE66D"), // 黄色
            Color.Parse("#A8E6CF"), // 浅绿色
            Color.Parse("#FF8B94"), // 粉红色
            Color.Parse("#B4A7D6"), // 紫色
            Color.Parse("#D4A574"), // 橙色
            Color.Parse("#85C1E9"), // 浅蓝色
            Color.Parse("#F8C471"), // 浅橙色
            Color.Parse("#BB8FCE"), // 浅紫色
            Color.Parse("#82E0AA"), // 浅绿色2
            Color.Parse("#F7DC6F"), // 浅黄色
            Color.Parse("#AED6F1"), // 浅蓝色2
            Color.Parse("#F1948A"), // 浅红色
            Color.Parse("#D7DBDD")  // 浅灰色
        };

        

        // 获取通道颜色（单通道或回退使用）
        private Color GetChannelColor(int channelId)
        {
            int colorIndex = (channelId - 1) % ColorPalette.Length;
            return ColorPalette[colorIndex];
        }

        // 为当前渲染生成一组明显区分的颜色
        private static List<Color> GenerateDistinctColors(int count)
        {
            // 高对比预设（色盲友好近似）
            var preset = new List<Color>
            {
                Color.Parse("#4477AA"), // 蓝
                Color.Parse("#EE6677"), // 红
                Color.Parse("#228833"), // 绿
                Color.Parse("#CCBB44"), // 黄褐
                Color.Parse("#66CCEE"), // 浅蓝
                Color.Parse("#AA3377"), // 紫红
                Color.Parse("#BBBBBB"), // 灰
                Color.Parse("#0099CC"), // 亮蓝
                Color.Parse("#DDCC77"), // 沙黄
                Color.Parse("#117733"), // 深绿
                Color.Parse("#332288"), // 深蓝紫
                Color.Parse("#88CCEE")  // 浅青
            };

            var colors = new List<Color>(count);
            for (int i = 0; i < Math.Min(count, preset.Count); i++)
                colors.Add(preset[i]);

            if (count > preset.Count)
            {
                int extra = count - preset.Count;
                for (int i = 0; i < extra; i++)
                {
                    double hue = (360.0 * i) / extra; // 平均分布色相
                    colors.Add(FromHsl(hue, 0.65, 0.55));
                }
            }
            return colors;
        }

        private static Color FromHsl(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360; // 归一化到[0,360)
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hh = h / 60.0;
            double x = c * (1 - Math.Abs(hh % 2 - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hh < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (hh < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (hh < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (hh < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (hh < 5) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            double m = l - c / 2.0;
            byte r = (byte)Math.Round((r1 + m) * 255.0);
            byte g = (byte)Math.Round((g1 + m) * 255.0);
            byte b = (byte)Math.Round((b1 + m) * 255.0);
            return Color.FromArgb(255, r, g, b);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var zoomX = GetZoomX?.Invoke() ?? 1.0f;
            var zoomY = GetZoomY?.Invoke() ?? 1.0f;
            var bounds = Bounds;
            double width = bounds.Width;
            double height = bounds.Height;

            // 背景：深色
            context.FillRectangle(new SolidColorBrush(Color.Parse("#121212")), bounds);

            // 网格与坐标轴
            var gridPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
            var axisPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")), 1.5);
            int gx = 8, gy = 4;
            for (int i = 1; i < gx; i++)
            {
                double gridX = width * i / gx;
                context.DrawLine(gridPen, new Point(gridX, 0), new Point(gridX, height));
            }
            for (int j = 1; j < gy; j++)
            {
                double gridY = height * j / gy;
                context.DrawLine(gridPen, new Point(0, gridY), new Point(width, gridY));
            }
            // 左边与底部坐标轴
            context.DrawLine(axisPen, new Point(0, 0), new Point(0, height));
            context.DrawLine(axisPen, new Point(0, height - 1), new Point(width, height - 1));

            if (width <= 0 || height <= 0)
                return;

            // 尝试获取多通道数据
            var multiChannelData = MultiChannelDataProvider?.Invoke();
            if (multiChannelData != null && multiChannelData.Any())
            {
                // 渲染多通道数据
                RenderMultiChannelData(context, multiChannelData, zoomX, zoomY, width, height);
            }
            else
            {
                // 回退到单通道数据
                var data = DataProvider?.Invoke() ?? Array.Empty<DH.Contracts.Models.CurvePoint>();
                if (data.Count >= 2)
                {
                    var color = GenerateDistinctColors(1)[0];
                    RenderSingleChannelData(context, data, zoomX, zoomY, width, height, color);
                }
            }
        }

        private void RenderMultiChannelData(DrawingContext context, Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> multiChannelData, float zoomX, float zoomY, double width, double height)
        {
            if (multiChannelData == null || multiChannelData.Count == 0)
                return;

            // 统一自适配：根据所有通道的数据长度与振幅，计算全局X步长与Y缩放
            double leftPad = 4.0, rightPad = 4.0;
            double usableWidth = Math.Max(1.0, width - leftPad - rightPad);
            int maxCount = 0;
            double globalMaxAbsY = 0.0;
            var orderedChannelIds = multiChannelData.Keys.OrderBy(id => id).ToList();
            foreach (var id in orderedChannelIds)
            {
                var d = multiChannelData[id];
                if (d == null || d.Count < 2) continue;
                maxCount = Math.Max(maxCount, d.Count);
                for (int i = 0; i < d.Count; i++)
                    globalMaxAbsY = Math.Max(globalMaxAbsY, Math.Abs(d[i].Y));
            }
            if (maxCount < 2) return;
            if (globalMaxAbsY < 1e-6) globalMaxAbsY = 1.0;

            double baseXStep = usableWidth / Math.Max(1, maxCount - 1);
            double xStep = (IsAutoFitX?.Invoke() ?? true) ? baseXStep : baseXStep * zoomX;

            double centerY = height / 2.0;
            double marginRatio = 0.90; // 让最大振幅占据高度的90%
            double baseScaleY = (centerY * marginRatio) / globalMaxAbsY;
            double scaleY = baseScaleY;
            if (!(IsAutoFitY?.Invoke() ?? true))
            {
                scaleY = baseScaleY * zoomY;
                if (scaleY > baseScaleY) scaleY = baseScaleY; // 夹紧，避免裁剪
            }

            // 生成唯一且明显区分的颜色集合（按通道排序保证稳定）
            var distinctColors = GenerateDistinctColors(orderedChannelIds.Count);

            // 绘制各通道：共享同一X步长和Y缩放，确保都完整显示
            for (int cidx = 0; cidx < orderedChannelIds.Count; cidx++)
            {
                int channelId = orderedChannelIds[cidx];
                var data = multiChannelData[channelId];
                if (data == null || data.Count < 2) continue;

                var color = distinctColors[cidx];
                var pen = new Pen(new SolidColorBrush(color), 1.5);

                double prevX = leftPad;
                double prevY = centerY - data[0].Y * scaleY;
                for (int i = 1; i < data.Count; i++)
                {
                    double x = leftPad + i * xStep;
                    double y = centerY - data[i].Y * scaleY;
                    context.DrawLine(pen, new Point(prevX, prevY), new Point(x, y));
                    prevX = x;
                    prevY = y;
                    if (x > width - rightPad) break; // 超出可用宽度则停止
                }
            }
        }

        private void RenderSingleChannelData(DrawingContext context, IReadOnlyList<DH.Contracts.Models.CurvePoint> data, float zoomX, float zoomY, double width, double height, Color color)
        {
            if (data.Count < 2)
                return;

            // 计算X步长：自适配下始终将全部点映射到可视宽度
            double leftPad = 4.0, rightPad = 4.0;
            double usableWidth = Math.Max(1.0, width - leftPad - rightPad);
            double baseXStep = usableWidth / Math.Max(1, data.Count - 1);
            double xStep = (IsAutoFitX?.Invoke() ?? true) ? baseXStep : baseXStep * zoomX;

            // 计算Y缩放：根据数据最大值自适配，避免上下裁剪；并保留手动缩放但不超过自适配上限
            double centerY = height / 2.0;
            double maxAbsY = 0.0;
            for (int i = 0; i < data.Count; i++)
                maxAbsY = Math.Max(maxAbsY, Math.Abs(data[i].Y));
            if (maxAbsY < 1e-6) maxAbsY = 1.0; // 防止除零

            double marginRatio = 0.90; // 占用高度的90%，避免顶边贴合
            double baseScaleY = (centerY * marginRatio) / maxAbsY;
            double scaleY = baseScaleY;
            if (!(IsAutoFitY?.Invoke() ?? true))
            {
                scaleY = baseScaleY * zoomY;
                // 保护：不超过自适配上限，避免裁剪
                if (scaleY > baseScaleY) scaleY = baseScaleY;
            }

            var pen = new Pen(new SolidColorBrush(color), 1.5);
            double prevX = leftPad;
            double prevY = centerY - data[0].Y * scaleY;

            for (int i = 1; i < data.Count; i++)
            {
                double x = leftPad + i * xStep;
                double y = centerY - data[i].Y * scaleY;
                context.DrawLine(pen, new Point(prevX, prevY), new Point(x, y));
                prevX = x;
                prevY = y;
                if (x > width - rightPad) break; // 超出可用宽度则停止
            }
        }
    }
}
