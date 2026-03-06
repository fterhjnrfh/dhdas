using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Avalonia.Skia;
using SkiaSharp;
using DH.Contracts.Models;

namespace DH.Client.App.Controls
{
    // 使用 SkiaSharp 渲染多通道曲线的控件
    public class SkiaMultiChannelView : Control
    {
        public Func<IReadOnlyList<CurvePoint>> DataProvider { get; set; } = () => Array.Empty<CurvePoint>();
        public Func<Dictionary<int, IReadOnlyList<CurvePoint>>> MultiChannelDataProvider { get; set; } = () => new Dictionary<int, IReadOnlyList<CurvePoint>>();
        public Func<float> GetZoomX { get; set; } = () => 1.0f;
        public Func<float> GetZoomY { get; set; } = () => 1.0f;
        public Func<bool> IsAutoFitX { get; set; } = () => true;
        public Func<bool> IsAutoFitY { get; set; } = () => true;
        
        // 新增：滚动模式（示波器模式）
        public bool ScrollMode { get; set; } = true; // 启用滚动模式，实现从左往右扫描效果
        public int ScrollWindowSize { get; set; } = 2000; // 滚动窗口大小（显示最近2000个数据点）
        
        // 反转X轴绘制：实现从左往右的扫描效果
        public bool ReverseXRendering { get; set; } = true; // 反转X轴，最新数据在左边
        
        // 示波器模式：从左到右逐点绘制
        public bool UseOscilloscopeMode { get; set; } = false; // 启用示波器扫描模式
        // 新增：可选的通道颜色映射（稳定颜色）
        public Dictionary<int, Color>? ChannelColorsMap { get; set; }
        public bool UseExtremaAggregation { get; set; } = true; // 使用极值聚合，保留尖峰/方波边沿
        public bool ShowLegend { get; set; } = true;            // 显示左上角图例

        // 坐标轴与网格渲染配置（可由外部界面统一设置，以保证一致性）
        public int DesiredXTicks { get; set; } = 8;   // 期望的主刻度数量（X）
        public int DesiredYTicks { get; set; } = 6;   // 期望的主刻度数量（Y）
        public Color GridMajorColor { get; set; } = Color.Parse("#303030");
        public Color GridMinorColor { get; set; } = Color.Parse("#242424");
        public Color AxisColor { get; set; } = Color.Parse("#3A3A3A");
        public float AxisThickness { get; set; } = 1.5f;
        public float GridMajorThickness { get; set; } = 1.0f;
        public float GridMinorThickness { get; set; } = 0.5f;
        public Func<double, string>? FormatXLabel { get; set; } = null; // 外部可注入格式化器，如时间轴
        public Func<double, string>? FormatYLabel { get; set; } = null; // 外部可注入格式化器，如单位/科学计数

        // 时间轴相关：将X轴从样本索引映射为时间
        public bool UseTimeAxis { get; set; } = true; // 开启时间格式标签
        public double SampleRateHz { get; set; } = 100;  // 采样率（Hz），>0 生效，默认 100 Hz
        public DateTime? StartTimestampUtc { get; set; } = null; // 起始时间（UTC，可选），为空则显示相对时间
        public bool ShowAbsoluteTime { get; set; } = false; // 为true且StartTimestampUtc有值时显示绝对时间
        
        // 时间轴滚动窗口：固定显示 N 秒的数据
        public double TimeWindowSeconds { get; set; } = 20.0;
        
        // 时间轴起始位置
        private double _timeWindowStartSeconds = 0.0;
        private bool _scrollAnimating = false;
        private DateTime _scrollAnimStart = DateTime.MinValue;
        private TimeSpan _scrollAnimDuration = TimeSpan.FromMilliseconds(240);
        private int _historyTrailWindows = 1;
        private float _historyTrailAlpha = 0.35f;
        private SKSurface? _offscreen;
        private SKImageInfo _offInfo;
        // 分层：背景(已完成) 与 前景(动态)
        private SKSurface? _bgSurface;
        private SKImageInfo _bgInfo;
        private SKSurface? _fgSurface;
        private SKImageInfo _fgInfo;
        private readonly Dictionary<int,int> _lastCommittedIdx = new();
        private double _lastWindowStartSeconds = 0.0;
        public double DynamicTailSeconds { get; set; } = 0.5; // 前景动态尾段长度
        public float ForegroundHighlightAlpha { get; set; } = 0.90f; // 前景高亮透明度
        public float ForegroundStrokeBoost { get; set; } = 0.6f; // 前景线条加粗
        // 记录坐标轴参数，供外层一次绘制
        private int _axStart;
        private int _axCount;
        private float _axLeftPad;
        private float _axUsableWidth;
        private float _axCenterY;
        private float _axScaleY;
        private int _axDataStartIdx;
        // 每个时间窗口锁定 Y 缩放，避免前景/背景缩放不一致造成断续
        private double _lockedWindowStartSeconds = double.NaN;
        private float _lockedScaleBaseY = 1.0f;
        private double _lockedMaxAbsY = 1.0;
        // 交互/外部触发的背景失效标记（需按当前缩放重建背景层）
        private bool _bgInvalidated = false;

        // 采样密度调节（>1提高可见细节；<1更强聚合）。不改变数据，仅影响绘制分箱。
        public double SamplingDensityFactor { get; set; } = 1.0;

        // 移动平均（SMA）可选开关与窗口（作用于绘制阶段的可视窗口数据）
        public bool EnableMovingAverage { get; set; } = false;
        public int MovingAverageWindow { get; set; } = 16;

        // 全局移动平均设置（由算法配置页驱动）
        public static bool GlobalEnableMovingAverage { get; private set; } = false;
        public static int GlobalMovingAverageWindow { get; private set; } = 16;
        private static event Action? GlobalSettingsChanged;

        public static void SetGlobalMovingAverage(bool enable, int window)
        {
            GlobalEnableMovingAverage = enable;
            GlobalMovingAverageWindow = Math.Max(1, window);
            GlobalSettingsChanged?.Invoke();
        }
        // 交互与视图窗口状态
        private bool _dragging;
        private Point _lastPt;
        private readonly List<SKPoint> _dragTrace = new();
        private DateTime _traceStart;
        private const int TraceMaxPoints = 120;

        private int _viewLeft = 0;           // 可视窗口左侧样本索引（基于最大长度数据）
        private int _viewCount = 0;          // 可视窗口样本数（根据缩放与宽度计算）
        private float _interactiveZoomX = 1.0f; // 交互缩放（X）
        private bool _interactiveAutoFitX = true; // 是否自适配 X（交互更改后置为 false）
        private const float MinZoomX = 0.1f;
        private const float MaxZoomX = 20000f; // 提升上限，放大更明显
        
        // 删除了示波器模式变量，改用时间轴滚动实现
        // 时间轴滚动会自动计算应显示的数据范围

        // 纵轴缩放交互
        private float _interactiveZoomY = 1.0f;
        private bool _interactiveAutoFitY = true;
        private const float MinZoomY = 0.1f;
        private const float MaxZoomY = 50f;

        // 简单缩放动画（滚轮触发）：120ms ease-out 插值
        private bool _animatingZoom;
        private float _zoomFrom;
        private float _zoomTo;
        private DateTime _animStart;
        private TimeSpan _animDuration = TimeSpan.FromMilliseconds(180);
        private readonly DispatcherTimer _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };

        // Y 缩放动画
        private bool _animatingZoomY;
        private float _zoomYFrom;
        private float _zoomYTo;

        // 跳转至末端动画状态
        private bool _jumping;
        private int _jumpStartLeft;
        private int _jumpTargetLeft;
        private DateTime _jumpStart;
        private TimeSpan _jumpDuration = TimeSpan.FromMilliseconds(600);
        public event Action<double>? JumpProgressChanged; // 0..100
        public event Action<bool>? JumpingStateChanged;   // true=进行中，false=结束

        public class ViewState
        {
            public float ZoomX { get; set; }
            public float ZoomY { get; set; } = 1.0f;
            public int ViewLeft { get; set; }
            public int ViewCount { get; set; }
        }
        public event Action<ViewState>? ViewStateChanged;

        private bool _loggedBackend = false;

        // Y 自适配窗口最大值缓存，降低高频重算导致的卡顿
        private int _yAutoLastStart = -1;
        private int _yAutoLastCount = -1;
        private double _yAutoLastMaxAbs = 1.0;
        private DateTime _yAutoLastTime = DateTime.MinValue;
        // Y 自适配平滑（避免细微抖动导致的闪屏）
        private double _yAutoSmoothMaxAbs = 1.0;
        private bool _yAutoSmoothInit = false;

        public SkiaMultiChannelView()
        {
            ClipToBounds = true;
            Focusable = true; // 确保可获得焦点并优先接收滚轮事件
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerMoved += OnPointerMoved;
            PointerWheelChanged += OnPointerWheelChanged;

            _animTimer.Tick += (s, e) =>
            {
                bool anyActive = false;
                if (_animatingZoom)
                {
                    anyActive = true;
                    double t = Math.Clamp((DateTime.Now - _animStart).TotalMilliseconds / _animDuration.TotalMilliseconds, 0.0, 1.0);
                    // ease-out：1 - (1 - t)^2
                    double eased = 1.0 - (1.0 - t) * (1.0 - t);
                    _interactiveZoomX = (float)(_zoomFrom + (_zoomTo - _zoomFrom) * eased);
                    if (t >= 1.0)
                    {
                        _animatingZoom = false;
                        _interactiveZoomX = _zoomTo;
                    }
                }

                if (_animatingZoomY)
                {
                    anyActive = true;
                    double t = Math.Clamp((DateTime.Now - _animStart).TotalMilliseconds / _animDuration.TotalMilliseconds, 0.0, 1.0);
                    double eased = 1.0 - (1.0 - t) * (1.0 - t);
                    _interactiveZoomY = (float)(_zoomYFrom + (_zoomYTo - _zoomYFrom) * eased);
                    if (t >= 1.0)
                    {
                        _animatingZoomY = false;
                        _interactiveZoomY = _zoomYTo;
                    }
                }

                if (_jumping)
                {
                    anyActive = true;
                    double t = Math.Clamp((DateTime.Now - _jumpStart).TotalMilliseconds / _jumpDuration.TotalMilliseconds, 0.0, 1.0);
                    // 使用三次缓出提升平滑度
                    double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                    int nextLeft = _jumpStartLeft + (int)Math.Round((_jumpTargetLeft - _jumpStartLeft) * eased);
                    _viewLeft = Math.Clamp(nextLeft, 0, Math.Max(0, GetMaxDataCount() - Math.Max(2, _viewCount)));
                    JumpProgressChanged?.Invoke(t * 100.0);
                    if (t >= 1.0)
                    {
                        _jumping = false;
                        _viewLeft = _jumpTargetLeft;
                        JumpingStateChanged?.Invoke(false);
                        JumpProgressChanged?.Invoke(100.0);
                    }
                }

                InvalidateVisual();
                ViewStateChanged?.Invoke(new ViewState { ZoomX = _interactiveZoomX, ZoomY = _interactiveZoomY, ViewLeft = _viewLeft, ViewCount = _viewCount });
                if (!anyActive) _animTimer.Stop();
            };

            // 订阅全局设置变化，并立即应用一次
            GlobalSettingsChanged += OnGlobalSettingsChanged;
            OnGlobalSettingsChanged();
        }

        private void OnGlobalSettingsChanged()
        {
            EnableMovingAverage = GlobalEnableMovingAverage;
            MovingAverageWindow = GlobalMovingAverageWindow;
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            GlobalSettingsChanged -= OnGlobalSettingsChanged;
            base.OnDetachedFromVisualTree(e);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.Custom(new DrawOp(new Rect(Bounds.Size), this));
        }

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly SkiaMultiChannelView _owner;
            public DrawOp(Rect bounds, SkiaMultiChannelView owner)
            {
                _bounds = bounds;
                _owner = owner;
            }
            public Rect Bounds => _bounds;
            public void Dispose() { }
            public bool HitTest(Point p) => _bounds.Contains(p);
            public bool Equals(ICustomDrawOperation? other) => false;
        
            public void Render(ImmediateDrawingContext context)
            {
                // 使用非泛型 TryGetFeature 以兼容当前 Avalonia 版本
                var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                using var lease = leaseFeature?.Lease();
                var canvas = lease?.SkCanvas;
                if (canvas is null) return;
        
                float width = (float)_bounds.Width;
                float height = (float)_bounds.Height;
                _owner.RenderSkia(canvas, width, height, lease);
            }
        }

        private void RenderSkia(SKCanvas canvas, float width, float height, ISkiaSharpApiLease lease)
        {
            if (!_loggedBackend)
            {
                bool gpu = lease?.GrContext is not null;
                string backend = lease?.GrContext?.Backend.ToString() ?? "Software";
                Console.WriteLine($"[Skia] Rendering: {(gpu ? "GPU" : "CPU")}  Backend={backend}");
                _loggedBackend = true;
            }
        
            if (width <= 0 || height <= 0) return;
        
            var info = new SKImageInfo((int)Math.Max(1, width), (int)Math.Max(1, height));
            if (_offscreen == null || _offInfo.Width != info.Width || _offInfo.Height != info.Height)
            {
                _offscreen?.Dispose();
                _offscreen = SKSurface.Create(info);
                _offInfo = info;
            }
            var target = _offscreen!.Canvas;
            target.Clear(new SKColor(0xFF, 0xFF, 0xFF));
            // 初始化分层画布
            if (_bgSurface == null || _bgInfo.Width != info.Width || _bgInfo.Height != info.Height)
            {
                _bgSurface?.Dispose();
                _bgSurface = SKSurface.Create(info);
                _bgInfo = info;
                _lastCommittedIdx.Clear();
                _bgSurface.Canvas.Clear(new SKColor(0,0,0,0)); // 透明
            }
            if (_fgSurface == null || _fgInfo.Width != info.Width || _fgInfo.Height != info.Height)
            {
                _fgSurface?.Dispose();
                _fgSurface = SKSurface.Create(info);
                _fgInfo = info;
            }
            _fgSurface.Canvas.Clear(new SKColor(0,0,0,0));
        
            // 网格与坐标轴（根据刻度生成器绘制）
            using var axis = new SKPaint { Color = ToSkColor(AxisColor), StrokeWidth = AxisThickness, IsAntialias = true };
            using var gridMajor = new SKPaint { Color = new SKColor(0xCC,0xCC,0xCC), StrokeWidth = GridMajorThickness, IsAntialias = false, PathEffect = SKPathEffect.CreateDash(new float[]{6f,6f}, 0) };
            using var gridMinor = new SKPaint { Color = new SKColor(0xE6,0xE6,0xE6), StrokeWidth = GridMinorThickness, IsAntialias = false, PathEffect = SKPathEffect.CreateDash(new float[]{3f,6f}, 0) };
            target.DrawLine(0, 0, 0, height, axis);
            target.DrawLine(0, height - 1, width, height - 1, axis);
        
            // 数据渲染（更新背景/前景层并在最终目标上合成）
            var multi = MultiChannelDataProvider?.Invoke();
            if (multi is { Count: > 0 })
            {
                RenderMulti(target, multi, width, height);
                DrawAxisAndGrid(target, width, height, _axStart, _axCount, _axLeftPad, _axUsableWidth, _axCenterY, _axScaleY, _axDataStartIdx);
            }
            else
            {
                var single = DataProvider?.Invoke() ?? Array.Empty<CurvePoint>();
                if (single.Count >= 2)
                {
                    var colors = GenerateDistinctColors(1);
                    RenderSingle(target, single, width, height, colors[0]);
                    // 单通道模式：RenderSingle 本身已经调用坐标轴绘制
                }
            }

            // 根据当前视窗绘制刻度与网格（放在数据后，避免覆盖粗线端帽）
            // 注意：RenderMulti/Single 会计算 start/count/scaleY/centerY，这里需要统一再绘制。
            // 为避免重复计算，将绘制轴的逻辑移到各自方法内调用。

            // 角落缩放比例显示（淡灰）
            using var text = new SKPaint { Color = new SKColor(120, 120, 120), IsAntialias = true, TextSize = 11f };
            float zoomIndicatorX = GetEffectiveZoomX();
            float zoomIndicatorY = GetEffectiveZoomY();
            var s = $"Zoom X {zoomIndicatorX:F2}x | Y {zoomIndicatorY:F2}x";
            target.DrawText(s, width - 190, 14, text);

            // 拖拽轨迹反馈（轻微淡出）
            if (_dragTrace.Count > 1)
            {
                double age = (DateTime.Now - _traceStart).TotalMilliseconds;
                byte alpha = (byte)Math.Clamp(180 - age * 0.3, 20, 180);
                using var pathPen = new SKPaint { Color = new SKColor(80, 160, 240, alpha), StrokeWidth = 1.0f, IsAntialias = true };
                for (int i = 1; i < _dragTrace.Count; i++)
                {
                    var p0 = _dragTrace[i - 1];
                    var p1 = _dragTrace[i];
                    target.DrawLine(p0.X, p0.Y, p1.X, p1.Y, pathPen);
                }
            }

            var snapshot = _offscreen!.Snapshot();
            canvas.DrawImage(snapshot, new SKPoint(0, 0));
            snapshot.Dispose();
        }

        private static string FormatAuto(double v, double span)
        {
            double av = Math.Max(1e-12, Math.Abs(span));
            if (av < 1e-3 || av >= 1e6) return v.ToString("0.###E+0");
            if (av < 1) return v.ToString("0.000");
            if (av < 100) return v.ToString("0.00");
            if (av < 10000) return v.ToString("0.0");
            return v.ToString("0");
        }

        private static string FormatTimeAuto(double seconds, DateTime? startUtc, bool absolute)
        {
            // 根据时间尺度选择格式：
            // <1s: 毫秒； <60s: 秒.毫秒； <1h: 分:秒； <1d: 时:分； 否则：月/日 或 年-月-日
            if (!absolute)
            {
                if (seconds < 1) return $"{seconds*1000:0} ms";
                if (seconds < 60) return $"{seconds:0.000} s";
                if (seconds < 3600) { var ts = TimeSpan.FromSeconds(seconds); return ts.ToString(@"mm\:ss"); }
                if (seconds < 86400) { var ts = TimeSpan.FromSeconds(seconds); return ts.ToString(@"hh\:mm"); }
                var d = TimeSpan.FromSeconds(seconds);
                return $"{(int)d.TotalDays} d";
            }
            else
            {
                var baseTime = startUtc ?? DateTime.UtcNow.Date;
                var t = baseTime.AddSeconds(seconds);
                var span = Math.Abs(seconds);
                if (span < 1) return t.ToLocalTime().ToString("HH:mm:ss.fff");
                if (span < 60) return t.ToLocalTime().ToString("HH:mm:ss");
                if (span < 3600) return t.ToLocalTime().ToString("HH:mm");
                if (span < 86400) return t.ToLocalTime().ToString("MM-dd HH:mm");
                if (span < 86400*30) return t.ToLocalTime().ToString("MM-dd");
                return t.ToLocalTime().ToString("yyyy-MM-dd");
            }
        }

        private void DrawAxisAndGrid(SKCanvas canvas, float width, float height,
            int startIndex, int sampleCount, float leftPad, float usableWidth,
            float centerY, float scaleY, int dataStartIdx = 0)
        {
            using var tickPen = new SKPaint { Color = new SKColor(0x88, 0x88, 0x88), StrokeWidth = 1f, IsAntialias = true };
            using var textPen = new SKPaint { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true, TextSize = 11f };

            // X 轴：根据期望刷度数生成 nice step
            int xTicks = Math.Max(2, DesiredXTicks);
            double xMinIdx = startIndex;
            double xMaxIdx = startIndex + Math.Max(1, sampleCount - 1);
            // 时间轴模式：显示从 _timeWindowStartSeconds 起始的实际时间
            bool timeAxis = UseTimeAxis && SampleRateHz > 0;
            double xMin, xMax;
            if (timeAxis)
            {
                // 时间轴模式：介于时间窗口的焘内的时间（秒）
                xMin = _timeWindowStartSeconds; // 窗口起始时间
                xMax = _timeWindowStartSeconds + TimeWindowSeconds; // 窗口结束时间
            }
            else
            {
                // 不使用时间轴模式：保持索引
                xMin = xMinIdx;
                xMax = xMaxIdx;
            }
            double xSpan = Math.Max(1e-12, xMax - xMin);
            (double xStep, double xStart) = NiceStepAndStart(xMin, xMax, xTicks);

            // X轴主网格（与Y轴统一样式）
            using var gridMajorPaint = new SKPaint { Color = ToSkColor(GridMajorColor), StrokeWidth = GridMajorThickness, IsAntialias = false };
            for (double xv = xStart; xv <= xMax + 0.5 * xStep; xv += xStep)
            {
                float xp = (float)(leftPad + (xv - xMin) / xSpan * usableWidth);
                canvas.DrawLine(xp, 0, xp, height, gridMajorPaint);
            }
            // 主刻度与标签（X）
            int xLabelSkip = Math.Max(1, (int)Math.Ceiling((float)MeasureLabelOverlap(width, xSpan, xStep, textPen)));
            int xi = 0;
            for (double xv = xStart; xv <= xMax + 0.5 * xStep; xv += xStep)
            {
                float xp = (float)(leftPad + (xv - xMin) / xSpan * usableWidth);
                canvas.DrawLine(xp, height - 6, xp, height - 1, tickPen);
                if (xi % xLabelSkip == 0)
                {
                    string label;
                if (timeAxis)
                    {
                        // 时间轴模式：直接显示实际时間位置（秒数）
                        double actualSeconds = xv;
                        label = FormatXLabel?.Invoke(actualSeconds) ?? FormatTimeAuto(actualSeconds, StartTimestampUtc, ShowAbsoluteTime);
                    }
                    else
                    {
                        label = FormatXLabel?.Invoke(xv) ?? FormatAuto(xv, xSpan);
                    }
                    canvas.DrawText(label, xp, height - 16, textPen);
                }
                xi++;
            }

            // Y 轴：根据可视范围生成
            int yTicks = Math.Max(2, DesiredYTicks);
            double ySpan = Math.Max(1e-6, (height - 1));
            // 以当前 scaleY 映射反算可视值域
            double vMin = (centerY - (height - 1)) / Math.Max(1e-6f, scaleY);
            double vMax = (centerY - 0) / Math.Max(1e-6f, scaleY);
            double yRange = vMax - vMin;
            (double yStep, double yStart) = NiceStepAndStart(vMin, vMax, yTicks);

            // Y轴主网格（与X轴统一样式）
            for (double yv = yStart; yv <= vMax + 0.5 * yStep; yv += yStep)
            {
                float yp = (float)(centerY - yv * scaleY);
                canvas.DrawLine(0, yp, width, yp, gridMajorPaint);
                // 刻度与标签
                canvas.DrawLine(0, yp, 6, yp, tickPen);
                string label = FormatYLabel?.Invoke(yv) ?? FormatAuto(yv, Math.Max(1e-6, yRange));
                canvas.DrawText(label, 10, yp + 4, textPen);
            }

            // 高亮零线
            float zeroY = (float)centerY;
            using var zeroPen = new SKPaint { Color = new SKColor(0xCC, 0x33, 0x33, 0xFF), StrokeWidth = 1.0f, IsAntialias = false, PathEffect = SKPathEffect.CreateDash(new float[]{8f,4f}, 0) };
            canvas.DrawLine(0, zeroY, width, zeroY, zeroPen);
        }

        private static (double step, double start) NiceStepAndStart(double min, double max, int ticks)
        {
            double span = Math.Max(1e-12, max - min);
            double rough = span / Math.Max(2, ticks);
            double pow10 = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double[] bases = { 1, 2, 5 };
            double step = bases.Select(b => b * pow10).OrderBy(s => Math.Abs(s - rough)).First();
            // 起始从向下取整到步长的倍数，确保包含负值区间
            double start = Math.Floor(min / step) * step;
            return (step, start);
        }

        private double MeasureLabelOverlap(float width, double span, double step, SKPaint textPaint)
        {
            // 估算标签过密比例：用固定字符宽度估计，返回应跳过的标签倍率
            // 目标：标签间隔至少 ~60px
            float targetPx = 60f;
            float perStep = (float)(width * (step / Math.Max(1e-12, span)));
            if (perStep >= targetPx) return 1.0;
            double k = Math.Ceiling(targetPx / Math.Max(1, perStep));
            return k;
        }

        private void RenderMulti(SKCanvas canvas, Dictionary<int, IReadOnlyList<CurvePoint>> multi, float width, float height)
        {
            float leftPad = 4f, rightPad = 4f;
            float usableWidth = Math.Max(1f, width - leftPad - rightPad);

            // 统一 X 步长与 Y 缩放
            int maxCount = 0;
            double globalMaxAbsY = 0.0;
            var ordered = multi.Keys.OrderBy(id => id).ToList();
            foreach (var id in ordered)
            {
                var d = multi[id];
                if (d == null || d.Count < 2) continue;
                maxCount = Math.Max(maxCount, d.Count);
                for (int i = 0; i < d.Count; i++)
                {
                    double val = d[i].Y;
                    // 过滤掉 NaN 和 Infinity
                    if (!double.IsNaN(val) && !double.IsInfinity(val))
                    {
                        globalMaxAbsY = Math.Max(globalMaxAbsY, Math.Abs(val));
                    }
                }
            }
            
            // 计算视口范围：
            // - 时间轴模式：使用固定时间窗口
            // - 索引模式：使用交互视口（支持滚轮缩放与拖动）
            int displayCount;
            int dataStartIdx;
            bool useTimeBasedX = UseTimeAxis && SampleRateHz > 0;
            if (useTimeBasedX)
            {
                double samplesPerSecond = SampleRateHz;
                int samplesInWindow = (int)(TimeWindowSeconds * samplesPerSecond);
                if (maxCount > samplesInWindow)
                {
                    int currentWindowIndex = (int)(_timeWindowStartSeconds / TimeWindowSeconds);
                    double expectedStartSeconds = currentWindowIndex * TimeWindowSeconds;
                    int expectedStartIdx = (int)(expectedStartSeconds * samplesPerSecond);
                    int nextWindowStartIdx = expectedStartIdx + samplesInWindow;
                    if (maxCount >= nextWindowStartIdx)
                    {
                        _timeWindowStartSeconds = (currentWindowIndex + 1) * TimeWindowSeconds;
                        if (Math.Abs(_timeWindowStartSeconds - _lastWindowStartSeconds) > 1e-9)
                        {
                            _bgSurface?.Canvas.Clear(new SKColor(0,0,0,0));
                            _lastCommittedIdx.Clear();
                            _lastWindowStartSeconds = _timeWindowStartSeconds;
                        }
                    }
                    dataStartIdx = (int)(_timeWindowStartSeconds * samplesPerSecond);
                    displayCount = samplesInWindow;
                }
                else
                {
                    _timeWindowStartSeconds = 0.0;
                    dataStartIdx = 0;
                    displayCount = maxCount;
                }
            }
            else
            {
                // 索引模式：尊重交互视口，支持滚轮缩放与拖动
                int pxW = Math.Max(1, (int)usableWidth);
                ComputeViewWindow(maxCount, pxW);
                dataStartIdx = Math.Clamp(_viewLeft, 0, Math.Max(0, maxCount - 2));
                displayCount = Math.Clamp(_viewCount, 2, Math.Max(2, maxCount - dataStartIdx));
            }
            
            if (displayCount < 2)
            {
                Console.WriteLine($"[RenderMulti] 数据不足，displayCount={displayCount}");
                return;
            }
            if (globalMaxAbsY < 1e-6)
            {
                Console.WriteLine($"[RenderMulti] globalMaxAbsY太小 ({globalMaxAbsY})，设置为默认值1.0");
                globalMaxAbsY = 1.0;
            }
            else
            {
                Console.WriteLine($"[RenderMulti] 通道数={ordered.Count}, maxCount={maxCount}, displayCount={displayCount}, startIdx={dataStartIdx}, globalMaxAbsY={globalMaxAbsY}");
            }

            int start = dataStartIdx;
            int count = displayCount;
            float xStep = usableWidth / Math.Max(1, count - 1);

            float centerY = height / 2f;
            double marginRatio = 0.90;
            bool autoFitY = (IsAutoFitY?.Invoke() ?? true) && _interactiveAutoFitY;
            if (double.IsNaN(_lockedWindowStartSeconds) || Math.Abs(_lockedWindowStartSeconds - _timeWindowStartSeconds) > 1e-9)
            {
                double est = autoFitY
                    ? SmoothMaxAbs(EstimateWindowMaxAbsYMulti(multi, ordered, start, count, usableWidth))
                    : globalMaxAbsY;
                est = Math.Max(1.0, est) * 1.10; // 适度留白，减少溢出概率
                _lockedMaxAbsY = est;
                _lockedScaleBaseY = (float)((centerY * marginRatio) / _lockedMaxAbsY);
                _lockedWindowStartSeconds = _timeWindowStartSeconds;
            }
            float scaleY = autoFitY ? _lockedScaleBaseY : _lockedScaleBaseY * GetEffectiveZoomY();
            bool needsBgRebuild = false;
            double candidateMax = EstimateWindowMaxAbsYMulti(multi, ordered, start, count, usableWidth);
            if (candidateMax > _lockedMaxAbsY * 1.08)
            {
                _lockedMaxAbsY = candidateMax * 1.10; // 加10%裕量避免再次溢出
                _lockedScaleBaseY = (float)((centerY * marginRatio) / _lockedMaxAbsY);
                scaleY = autoFitY ? _lockedScaleBaseY : _lockedScaleBaseY * GetEffectiveZoomY();
                needsBgRebuild = true;
            }
            // 首窗口内，为避免早期锁定变化导致断续，数据未超过窗口大小时适度重建背景
            if (_timeWindowStartSeconds == 0.0 && UseTimeAxis && SampleRateHz > 0)
            {
                int samplesInWindowBootstrap = (int)(TimeWindowSeconds * SampleRateHz);
                if (maxCount < Math.Min(samplesInWindowBootstrap, (int)Math.Max(32, usableWidth * 2)))
                    needsBgRebuild = true;
            }
            // 用户交互导致坐标变化时，强制重建背景层
            if (_bgInvalidated)
            {
                needsBgRebuild = true; _bgInvalidated = false;
            }

            var dstBg = _bgSurface!.Canvas;
            var dstFg = _fgSurface!.Canvas;
            if (!useTimeBasedX)
            {
                dstBg.Clear(new SKColor(0,0,0,0));
                dstFg.Clear(new SKColor(0,0,0,0));
            }
            
            double sampleRateForX = useTimeBasedX ? SampleRateHz : 1.0;
            
            for (int idx = 0; idx < ordered.Count; idx++)
            {
                int channelId = ordered[idx];
                var data = multi[channelId];
                if (data == null || data.Count < 2)
                {
                    continue;
                }

                // 时间轴模式：只显示当前窗口内的数据点
                int dataEndIdx = data.Count;
                if (dataEndIdx < 2) continue;
                
                // 计算当前窗口内实际有效的数据范围
                int actualEndIdx = Math.Min(start + count, dataEndIdx);
                int actualCount = actualEndIdx - start;
                
                if (actualCount < 2) continue;

                // 使用基于通道ID的固定颜色映射
                Color useColor = (ChannelColorsMap != null && ChannelColorsMap.TryGetValue(channelId, out var mapped))
                    ? mapped
                    : GetColorByChannelId(channelId);
                int pxCount = (int)Math.Floor(usableWidth);
                if (pxCount <= 0) continue;
                int lastIdx = Math.Min(start + actualCount - 1, dataEndIdx - 1);

                int tailSamples = Math.Max(2, (int)Math.Round(DynamicTailSeconds * SampleRateHz));
                int commitStart;
                int commitEnd;
                // 静态数据（索引模式下的文件查看）：每次交互都完整重绘窗口，避免仅保留尾段导致“右侧堆叠”
                if (!useTimeBasedX)
                {
                    commitStart = start;
                    commitEnd = lastIdx;
                }
                else
                {
                    if (needsBgRebuild)
                    {
                        commitStart = start;
                        commitEnd = Math.Max(start, lastIdx - tailSamples);
                    }
                    else
                    {
                        commitStart = _lastCommittedIdx.TryGetValue(channelId, out var lastCommit) ? Math.Max(start, lastCommit) : start;
                        commitEnd = Math.Max(commitStart, Math.Max(start, lastIdx - tailSamples));
                    }
                }
                int dynStart = commitEnd;
                int dynEnd = lastIdx;

                // 背景层：增量提交新完成段
                if (commitEnd > commitStart)
                {
                    int binsBg = Math.Max(2, (int)Math.Round(pxCount * SamplingDensityFactor));
                    var binBg = BinRanges(commitStart, commitEnd, binsBg);
                    using var penBg = new SKPaint { Color = ToSkColor(useColor), StrokeWidth = 2.5f, IsAntialias = false };
                    if (UseExtremaAggregation)
                    {
                        using var extBg = new SKPaint { Color = ToSkColor(useColor), StrokeWidth = 2.0f, IsAntialias = false };
                        for (int i = 0; i < binBg.Count; i++)
                        {
                            var (b0, b1) = binBg[i];
                            double min = double.MaxValue, max = double.MinValue;
                            for (int j = b0; j <= b1 && j <= commitEnd && j < dataEndIdx; j++) { var v = data[j].Y; if (v < min) min = v; if (v > max) max = v; }
                            // 时间轴模式：基于实际时间计算X坐标，而不是相对索引
                            float x;
                            if (useTimeBasedX)
                            {
                                // 计算数据点的实际时间位置（秒）
                                double sampleTime = b0 / sampleRateForX;
                                // 映射到画布上的X坐标
                                x = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                            }
                            else
                            {
                                x = leftPad + (float)((double)(b0 - start) / Math.Max(1, count - 1) * usableWidth);
                            }
                            float y0 = centerY - (float)min * scaleY;
                            float y1 = centerY - (float)max * scaleY;
                            dstBg.DrawLine(x, y0, x, y1, extBg);
                        }
                    }
                    double[] rep = new double[binBg.Count]; for (int i = 0; i < binBg.Count; i++) rep[i] = data[binBg[i].Item2].Y;
                    float prevX, prevY;
                    if (useTimeBasedX)
                    {
                        double sampleTime = binBg[0].Item2 / sampleRateForX;
                        prevX = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                    }
                    else
                    {
                        prevX = leftPad + (float)((double)(binBg[0].Item2 - start) / Math.Max(1, count - 1) * usableWidth);
                    }
                    prevY = centerY - (float)rep[0] * scaleY;
                    for (int i = 1; i < binBg.Count; i++)
                    {
                        float x;
                        if (useTimeBasedX)
                        {
                            double sampleTime = binBg[i].Item2 / sampleRateForX;
                            x = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                        }
                        else
                        {
                            x = leftPad + (float)((double)(binBg[i].Item2 - start) / Math.Max(1, count - 1) * usableWidth);
                        }
                        float y = centerY - (float)rep[i] * scaleY;
                        dstBg.DrawLine(prevX, prevY, x, y, penBg);
                        prevX = x; prevY = y;
                    }
                    _lastCommittedIdx[channelId] = commitEnd;
                }

                // 前景层：绘制当前动态尾段（时间轴模式下保留动态高亮；索引模式下不绘制尾段避免产生错觉）
                if (useTimeBasedX && dynEnd > dynStart)
                {
                    int binsFg = Math.Max(2, (int)Math.Round(pxCount * SamplingDensityFactor));
                    var binFg = BinRanges(dynStart, dynEnd, binsFg);
                    var fgColor = ToSkColor(useColor).WithAlpha((byte)(255 * ForegroundHighlightAlpha));
                    using var penFg = new SKPaint { Color = fgColor, StrokeWidth = 2.5f + ForegroundStrokeBoost, IsAntialias = true };
                    if (UseExtremaAggregation)
                    {
                        using var extFg = new SKPaint { Color = fgColor, StrokeWidth = 2.0f + ForegroundStrokeBoost * 0.5f, IsAntialias = false };
                        for (int i = 0; i < binFg.Count; i++)
                        {
                            var (b0, b1) = binFg[i];
                            double min = double.MaxValue, max = double.MinValue;
                            for (int j = b0; j <= b1 && j <= dynEnd && j < dataEndIdx; j++) { var v = data[j].Y; if (v < min) min = v; if (v > max) max = v; }
                            // 时间轴模式：基于实际时间计算X坐标
                            float x;
                            if (useTimeBasedX)
                            {
                                double sampleTime = b0 / sampleRateForX;
                                x = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                            }
                            else
                            {
                                x = leftPad + (float)((double)(b0 - start) / Math.Max(1, count - 1) * usableWidth);
                            }
                            float y0 = centerY - (float)min * scaleY;
                            float y1 = centerY - (float)max * scaleY;
                            dstFg.DrawLine(x, y0, x, y1, extFg);
                        }
                    }
                    double[] repF = new double[binFg.Count]; for (int i = 0; i < binFg.Count; i++) repF[i] = data[binFg[i].Item2].Y;
                    float prevXF, prevYF;
                    if (useTimeBasedX)
                    {
                        double sampleTime = binFg[0].Item2 / sampleRateForX;
                        prevXF = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                    }
                    else
                    {
                        prevXF = leftPad + (float)((double)(binFg[0].Item2 - start) / Math.Max(1, count - 1) * usableWidth);
                    }
                    prevYF = centerY - (float)repF[0] * scaleY;
                    for (int i = 1; i < binFg.Count; i++)
                    {
                        float x;
                        if (useTimeBasedX)
                        {
                            double sampleTime = binFg[i].Item2 / sampleRateForX;
                            x = leftPad + (float)((sampleTime - _timeWindowStartSeconds) / TimeWindowSeconds * usableWidth);
                        }
                        else
                        {
                            x = leftPad + (float)((double)(binFg[i].Item2 - start) / Math.Max(1, count - 1) * usableWidth);
                        }
                        float y = centerY - (float)repF[i] * scaleY;
                        dstFg.DrawLine(prevXF, prevYF, x, y, penFg);
                        prevXF = x; prevYF = y;
                    }
                }
            }

            // 合成图层到目标
            var imgBg = _bgSurface!.Snapshot();
            canvas.DrawImage(imgBg, new SKPoint(0, 0));
            imgBg.Dispose();
            var imgFg = _fgSurface!.Snapshot();
            canvas.DrawImage(imgFg, new SKPoint(0, 0));
            imgFg.Dispose();

            // 记录坐标轴参数，供外层一次绘制
            _axStart = start; _axCount = count; _axLeftPad = leftPad; _axUsableWidth = usableWidth; _axCenterY = centerY; _axScaleY = scaleY; _axDataStartIdx = dataStartIdx;

            // 图例（左上角）
            if (ShowLegend)
            {
                using var txt = new SKPaint { Color = new SKColor(0x33, 0x33, 0x33), IsAntialias = true, TextSize = 12f };
                float lx = 8f, ly = 10f; float box = 10f; float gap = 4f;
                int idx = 0;
                foreach (var ch in ordered)
                {
                    // 使用基于通道ID的固定颜色
                    var col = ToSkColor(GetColorByChannelId(ch));
                    using var swatch = new SKPaint { Color = col, StrokeWidth = 4f };
                    canvas.DrawLine(lx, ly + idx * 18, lx + box, ly + idx * 18, swatch);
                    int dev = ch / 100; int cno = ch % 100;
                    string label = DH.Contracts.ChannelNaming.ChannelName(dev, cno);
                    canvas.DrawText(label, lx + box + gap, ly + 4 + idx * 18, txt);
                    idx++;
                }
            }
        }


        private void RenderSingle(SKCanvas canvas, IReadOnlyList<CurvePoint> data, float width, float height, Color color)
        {
            if (data.Count < 2) return;

            float leftPad = 4f, rightPad = 4f;
            float usableWidth = Math.Max(1f, width - leftPad - rightPad);
            // 计算视图窗口
            ComputeViewWindow(data.Count, (int)usableWidth);
            int start = Math.Clamp(_viewLeft, 0, Math.Max(0, data.Count - 2));
            int count = Math.Clamp(_viewCount, 2, data.Count - start);
            float xStep = usableWidth / Math.Max(1, count - 1);

            float centerY = height / 2f;
            // 计算 Y 轴缩放：若自适配则按当前窗口估算最大值
            double marginRatio = 0.90;
            bool autoFitY = (IsAutoFitY?.Invoke() ?? true) && _interactiveAutoFitY;
            double windowMaxAbsY = autoFitY
                ? SmoothMaxAbs(EstimateWindowMaxAbsYSingle(data, start, count, usableWidth))
                : EstimateGlobalMaxAbsYSingle(data);
            // 留出5%上下边距
            windowMaxAbsY = windowMaxAbsY * 1.05;
            float baseScaleY = (float)((centerY * marginRatio) / windowMaxAbsY);
            float scaleY = autoFitY ? baseScaleY : baseScaleY * GetEffectiveZoomY();

            using var pen = new SKPaint { Color = ToSkColor(color), StrokeWidth = 2.5f, IsAntialias = false };
            // 像素级抽样
            int pxCount = (int)Math.Floor(usableWidth);
            if (pxCount <= 0) return;
            double spp = (double)count / Math.Max(1, (int)Math.Round(pxCount * SamplingDensityFactor));
            int lastIdx = Math.Min(start + count - 1, data.Count - 1);

            // 可选应用移动平均（基于可视窗口的代表采样点序列）
            int bins = Math.Max(2, (int)Math.Round(pxCount * SamplingDensityFactor));
            var binRanges = BinRanges(start, lastIdx, bins);
            if (UseExtremaAggregation)
            {
                using var extPen = new SKPaint { Color = ToSkColor(color), StrokeWidth = Math.Max(1f, pen.StrokeWidth - 0.3f), IsAntialias = false };
                for (int i = 0; i < binRanges.Count; i++)
                {
                    var (b0, b1) = binRanges[i];
                    double min = double.MaxValue, max = double.MinValue;
                    for (int j = b0; j <= b1; j++) { var v = data[j].Y; if (v < min) min = v; if (v > max) max = v; }
                    // 支持X轴反转
                    float x = ReverseXRendering && ScrollMode 
                        ? leftPad + (binRanges.Count - 1 - i) * (usableWidth / (binRanges.Count - 1))
                        : leftPad + i * (usableWidth / (binRanges.Count - 1));
                    float y0 = centerY - (float)min * scaleY;
                    float y1 = centerY - (float)max * scaleY;
                    canvas.DrawLine(x, y0, x, y1, extPen);
                }
            }
            var repYs = new double[binRanges.Count];
            for (int i = 0; i < binRanges.Count; i++) repYs[i] = data[binRanges[i].Item2].Y;
            
            // 支持X轴反转：实现从左往右的扫描效果
            if (ReverseXRendering && ScrollMode)
            {
                // 反转绘制：最新数据（索引最大）在最左边
                float prevX = leftPad + (binRanges.Count - 1) * (usableWidth / (binRanges.Count - 1));
                float prevY = centerY - (float)repYs[binRanges.Count - 1] * scaleY;
                for (int ix = binRanges.Count - 2; ix >= 0; ix--)
                {
                    float x = leftPad + ix * (usableWidth / (binRanges.Count - 1));
                    float y = centerY - (float)repYs[ix] * scaleY;
                    canvas.DrawLine(prevX, prevY, x, y, pen);
                    prevX = x; prevY = y;
                }
            }
            else
            {
                // 正常绘制：最旧数据在最左边
                float prevX = leftPad;
                float prevY = centerY - (float)repYs[0] * scaleY;
                for (int ix = 1; ix < binRanges.Count; ix++)
                {
                    float x = leftPad + ix * (usableWidth / (binRanges.Count - 1));
                    float y = centerY - (float)repYs[ix] * scaleY;
                    canvas.DrawLine(prevX, prevY, x, y, pen);
                    prevX = x; prevY = y;
                }
            }

            // 坐标轴与网格（按戱能剃度绘制）
            DrawAxisAndGrid(canvas, width, height, start, count, leftPad, usableWidth, centerY, scaleY, 0);
        }

        private void ComputeViewWindow(int maxCount, int pixelWidth)
        {
            // 基本保护：数据不足或像素宽度异常时避免异常计算
            if (maxCount <= 0)
            {
                _viewCount = 0;
                _viewLeft = 0;
                return;
            }

            // 当像素宽度不可用（0 或负值）时，回退到显示完整范围以避免后续采样错误
            if (pixelWidth <= 0)
            {
                _viewCount = Math.Clamp(maxCount, 2, maxCount);
                _viewLeft = 0;
                return;
            }

            bool autoFit = (IsAutoFitX?.Invoke() ?? true) && _interactiveAutoFitX;
            float zoom = GetEffectiveZoomX();
            
            // 示波器模式（优先）：从左到右逐点绘制
            // 窗口始终固定在起始位置(viewLeft=0)，新数据从右边追加
            if (UseOscilloscopeMode && autoFit)
            {
                int windowSize = Math.Min(ScrollWindowSize, maxCount);
                _viewCount = Math.Max(2, windowSize);
                _viewLeft = 0;  // 窗口固定在起始位置，实现从左往右扫描
                
                Console.WriteLine($"[OscilloscopeMode] maxCount={maxCount}, viewLeft={_viewLeft}, viewCount={_viewCount}");
                return;
            }
            
            // 非示波器模式下的自适配
            if (autoFit)
            {
                // 自适配：显示完整数据范围
                _viewCount = Math.Clamp(maxCount, 2, maxCount);
                _viewLeft = 0;
            }
            else
            {
                // 非自适配：依据缩放计算视口样本数（zoom 越大视窗越小）
                int desired = (int)Math.Clamp(Math.Round(maxCount / Math.Clamp(zoom, MinZoomX, MaxZoomX)), 2, maxCount);
                _viewCount = desired;
                _viewLeft = Math.Clamp(_viewLeft, 0, Math.Max(0, maxCount - _viewCount));
            }
        }

        private float GetEffectiveZoomX()
        {
            // 若外部提供委托，则优先使用（兼容旧用法）；否则使用内部交互缩放
            float ext = 1.0f;
            try { ext = GetZoomX?.Invoke() ?? 1.0f; } catch { ext = 1.0f; }
            return Math.Clamp(ext * _interactiveZoomX, MinZoomX, MaxZoomX);
        }

        private float GetEffectiveZoomY()
        {
            float ext = 1.0f;
            try { ext = GetZoomY?.Invoke() ?? 1.0f; } catch { ext = 1.0f; }
            return Math.Clamp(ext * _interactiveZoomY, MinZoomY, MaxZoomY);
        }

        private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            // 时间轴模式下禁止鼠标拖动交互，避免与时间窗口滚动冲突
            if (UseTimeAxis && SampleRateHz > 0)
            {
                return;
            }
            
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _dragging = true;
                _lastPt = e.GetPosition(this);
                e.Pointer.Capture(this);
                _interactiveAutoFitX = false; // 交互开始后退出自适配
                _traceStart = DateTime.Now;
                _dragTrace.Clear();
                _dragTrace.Add(new SKPoint((float)_lastPt.X, (float)_lastPt.Y));
            }
        }

        private void OnPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            // 拖拽轨迹在短时间内淡出
            _traceStart = DateTime.Now;
            InvalidateVisual();
        }

        private void OnPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
        {
            // 时间轴模式下禁止鼠标拖动交互
            if (UseTimeAxis && SampleRateHz > 0)
            {
                return;
            }
            
            if (!_dragging) return;
            var p = e.GetPosition(this);
            double dx = p.X - _lastPt.X;
            double dy = p.Y - _lastPt.Y;
            _lastPt = p;
            _dragTrace.Add(new SKPoint((float)p.X, (float)p.Y));
            if (_dragTrace.Count > TraceMaxPoints)
                _dragTrace.RemoveAt(0);

            // 根据像素偏移换算样本偏移：每像素视口样本 = _viewCount / 绘图区宽度
            float leftPad = 4f, rightPad = 4f;
            int plotW = Math.Max(1, (int)(Bounds.Width - leftPad - rightPad));
            int maxCount = GetMaxDataCount();
            if (maxCount < 2 || plotW <= 1)
            {
                // 数据不足或绘图区太窄时跳过交互计算
                InvalidateVisual();
                return;
            }
            ComputeViewWindow(maxCount, plotW);
            if (_viewCount <= 0)
            {
                InvalidateVisual();
                return;
            }
            double spp = (double)_viewCount / Math.Max(1, plotW);
            int dSamples = (int)Math.Round(-dx * spp); // 右拖=看更早 => 左索引增加
            int maxLeft = Math.Max(0, maxCount - _viewCount);
            _viewLeft = Math.Clamp(_viewLeft + dSamples, 0, maxLeft);

            // 垂直拖拽用于调节纵轴缩放（更直观的上下缩放）
            if (Math.Abs(dy) > 0.0)
            {
                _interactiveAutoFitY = false;
                // 将像素位移映射为缩放因子：每 12 像素约 1.25 倍
                double steps = -dy / 12.0; // 上拖(负dy) => 放大
                float factorY = (float)Math.Pow(1.25, steps);
                _interactiveZoomY = Math.Clamp(_interactiveZoomY * factorY, MinZoomY, MaxZoomY);
                _bgInvalidated = true;
            }
            InvalidateVisual();
            ViewStateChanged?.Invoke(new ViewState { ZoomX = _interactiveZoomX, ZoomY = _interactiveZoomY, ViewLeft = _viewLeft, ViewCount = _viewCount });
        }

        private void OnPointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
        {
            // 时间轴模式下禁止滚轮缩放交互，避免与时间窗口滚动冲突
            if (UseTimeAxis && SampleRateHz > 0)
            {
                e.Handled = true;
                return;
            }
            
            // 判断是否缩放 Y：Ctrl/Shift + 滚轮；否则默认缩放 X
            var mods = e.KeyModifiers;
            bool zoomYByWheel = mods.HasFlag(Avalonia.Input.KeyModifiers.Control) || mods.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            var mousePos = e.GetPosition(this);
            if (zoomYByWheel)
            {
                _interactiveAutoFitY = false;
                float factorY = e.Delta.Y > 0 ? 1.25f : 0.8f;
                float oldY = _interactiveZoomY;
                float newY = Math.Clamp(oldY * factorY, MinZoomY, MaxZoomY);
                _zoomYFrom = oldY; _zoomYTo = newY; _animStart = DateTime.Now; _animatingZoomY = true; _animTimer.Start();
                Console.WriteLine($"[Wheel] Zoom Y(anchor at x={mousePos.X:F0}): {oldY:F2}->{newY:F2}");
                InvalidateVisual();
                ViewStateChanged?.Invoke(new ViewState { ZoomX = _interactiveZoomX, ZoomY = _interactiveZoomY, ViewLeft = _viewLeft, ViewCount = _viewCount });
                _bgInvalidated = true;
                e.Handled = true;
                return;
            }

            // 上滚放大，下滚缩小（以鼠标所在位置为锚点）
            
            // 如果正在进行缩放动画，先取消并使用目标值
            if (_animatingZoom)
            {
                _animatingZoom = false;
                _interactiveZoomX = _zoomTo; // 使用动画目标值作为基准
            }
            
            float factor = e.Delta.Y > 0 ? 1.25f : 0.8f;
            float oldZoom = _interactiveZoomX;
            float newZoom = Math.Clamp(oldZoom * factor, MinZoomX, MaxZoomX);
            
            // 以鼠标位置为缩放中心，调整 viewLeft 保持锚点
            float leftPad = 4f;
            int plotW = Math.Max(1, (int)(Bounds.Width - leftPad - 4));
            int maxCount = GetMaxDataCount();
            if (maxCount < 2 || plotW <= 1)
            {
                // 数据不足或绘图区太窄，跳过缩放交互
                InvalidateVisual();
                return;
            }
            
            // 先计算当前视口状态（使用当前的自动适配状态）
            ComputeViewWindow(maxCount, plotW);
            if (_viewCount <= 0)
            {
                InvalidateVisual();
                return;
            }
            
            int x0 = (int)leftPad;
            int xInPlot = Math.Clamp((int)e.GetPosition(this).X - x0, 0, Math.Max(1, plotW) - 1);
            int anchor = _viewLeft + (int)Math.Round((double)xInPlot / Math.Max(1, plotW) * _viewCount);

            // 现在禁用自动适配并应用新缩放
            _interactiveAutoFitX = false;
            _interactiveZoomX = newZoom;
            
            // 重新计算视口并基于锚点调整 left
            ComputeViewWindow(maxCount, plotW);
            int newLeft = anchor - (int)Math.Round((double)xInPlot / Math.Max(1, plotW) * _viewCount);
            int maxLeft = Math.Max(0, maxCount - _viewCount);
            _viewLeft = Math.Clamp(newLeft, 0, maxLeft);

            // 直接应用缩放，不使用动画（避免快速滚轮时的累积误差）
            Console.WriteLine($"[Wheel] Zoom X: deltaY={e.Delta.Y}, plotW={plotW}, maxCount={maxCount}, oldZoom={oldZoom:F2} -> newZoom={newZoom:F2}, viewLeft={_viewLeft}, viewCount={_viewCount}");

            InvalidateVisual();
            ViewStateChanged?.Invoke(new ViewState { ZoomX = _interactiveZoomX, ZoomY = _interactiveZoomY, ViewLeft = _viewLeft, ViewCount = _viewCount });
            e.Handled = true;
            _bgInvalidated = true;
        }

        private int GetMaxDataCount()
        {
            var multi = MultiChannelDataProvider?.Invoke();
            if (multi is { Count: > 0 })
            {
                int mc = 0;
                foreach (var kv in multi)
                {
                    var d = kv.Value; if (d == null) continue;
                    mc = Math.Max(mc, d.Count);
                }
                return mc;
            }
            var single = DataProvider?.Invoke() ?? Array.Empty<CurvePoint>();
            return single.Count;
        }

        public void ResetView()
        {
            _interactiveAutoFitX = true;
            _interactiveZoomX = 1.0f;
            _interactiveAutoFitY = true;
            _interactiveZoomY = 1.0f;
            _viewLeft = 0;
            _dragTrace.Clear();
            _yAutoSmoothInit = false;
            _bgInvalidated = true;
            InvalidateVisual();
            ViewStateChanged?.Invoke(new ViewState { ZoomX = _interactiveZoomX, ZoomY = _interactiveZoomY, ViewLeft = _viewLeft, ViewCount = _viewCount });
        }

        public void SetViewState(ViewState state)
        {
            if (state is null) return;
            _interactiveAutoFitX = false;
            _interactiveZoomX = Math.Clamp(state.ZoomX, MinZoomX, MaxZoomX);
            _interactiveAutoFitY = false;
            _interactiveZoomY = Math.Clamp(state.ZoomY, MinZoomY, MaxZoomY);
            _viewLeft = Math.Max(0, state.ViewLeft);
            _viewCount = Math.Max(2, state.ViewCount);
            InvalidateVisual();
        }

        // 公共方法：获取总样本数（供外部按需使用）
        public int GetTotalCount() => GetMaxDataCount();

        // 平滑跳转到末端（默认 600ms，结束后启用 Y 自适配）
        public void JumpToEndSmooth(TimeSpan? duration = null)
        {
            int maxCount = GetMaxDataCount();
            float leftPad = 4f, rightPad = 4f;
            int plotW = Math.Max(1, (int)(Bounds.Width - leftPad - rightPad));
            ComputeViewWindow(maxCount, plotW);
            int maxLeft = Math.Max(0, maxCount - Math.Max(2, _viewCount));

            _jumpStartLeft = _viewLeft;
            _jumpTargetLeft = maxLeft;
            _jumpStart = DateTime.Now;
            _jumpDuration = duration ?? TimeSpan.FromMilliseconds(600);
            _jumping = true;

            // 跳转过程中禁用 X 自适配，结束后启用 Y 自适配以突出末端特征
            _interactiveAutoFitX = false;
            _interactiveAutoFitY = true;
            JumpingStateChanged?.Invoke(true);
            JumpProgressChanged?.Invoke(0.0);
            _animTimer.Start();
            InvalidateVisual();
        }

        private static List<Color> GenerateDistinctColors(int count)
        {
            var preset = new List<Color>
            {
                Color.Parse("#4477AA"), Color.Parse("#EE6677"), Color.Parse("#228833"), Color.Parse("#CCBB44"),
                Color.Parse("#66CCEE"), Color.Parse("#AA3377"), Color.Parse("#BBBBBB"), Color.Parse("#0099CC"),
                Color.Parse("#DDCC77"), Color.Parse("#117733"), Color.Parse("#332288"), Color.Parse("#88CCEE")
            };
            var colors = new List<Color>(count);
            for (int i = 0; i < Math.Min(count, preset.Count); i++) colors.Add(preset[i]);
            if (count > preset.Count)
            {
                int extra = count - preset.Count;
                for (int i = 0; i < extra; i++)
                {
                    double hue = (360.0 * i) / extra;
                    colors.Add(FromHsl(hue, 0.65, 0.55));
                }
            }
            return colors;
        }

        // 更贴近参考配色：红、蓝、绿优先
        private static List<Color> GeneratePresetPalette(int count)
        {
            var preset = new List<Color>
            {
                Color.Parse("#D62728"), // 红
                Color.Parse("#4169E1"), // 蓝
                Color.Parse("#2CA02C"), // 绿
                Color.Parse("#CCBB44"), Color.Parse("#66CCEE"), Color.Parse("#AA3377"), Color.Parse("#BBBBBB")
            };
            var colors = new List<Color>(count);
            for (int i = 0; i < Math.Min(count, preset.Count); i++) colors.Add(preset[i]);
            if (count > preset.Count)
            {
                int extra = count - preset.Count;
                for (int i = 0; i < extra; i++)
                {
                    double hue = (360.0 * i) / extra;
                    colors.Add(FromHsl(hue, 0.65, 0.55));
                }
            }
            return colors;
        }

        // 根据通道ID获取固定颜色（确保同一通道ID始终返回相同颜色）
        private static Color GetColorByChannelId(int channelId)
        {
            var palette = new List<Color>
            {
                Color.Parse("#D62728"), // 红
                Color.Parse("#4169E1"), // 蓝
                Color.Parse("#2CA02C"), // 绿
                Color.Parse("#FF8C00"), // 深橙
                Color.Parse("#9467BD"), // 紫色
                Color.Parse("#8B4513"), // 褐色
                Color.Parse("#E377C2"), // 粉色
                Color.Parse("#7F7F7F"), // 灰色
                Color.Parse("#BCBD22"), // 黄绿
                Color.Parse("#17BECF"), // 青色
                Color.Parse("#FF6347"), // 番茄红
                Color.Parse("#4682B4"), // 钢蓝
                Color.Parse("#32CD32"), // 酸橙绿
                Color.Parse("#FF69B4"), // 热粉
                Color.Parse("#CD5C5C"), // 印度红
                Color.Parse("#4169E1"), // 皇家蓝
            };
            
            // 使用通道ID对调色板长度取模，确保固定映射
            int colorIndex = channelId % palette.Count;
            return palette[colorIndex];
        }

        private static Color FromHsl(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360;
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

        private static SKColor ToSkColor(Color c) => new SKColor(c.R, c.G, c.B, c.A);

        // 从start..end均匀抽取bins个索引（含首尾）
        private static List<int> SampleIndices(int start, int end, int bins)
        {
            bins = Math.Max(2, bins);
            int len = Math.Max(1, end - start + 1);
            var list = new List<int>(bins);
            for (int i = 0; i < bins; i++)
            {
                int idx = start + (int)Math.Round((double)i / (bins - 1) * (len - 1));
                list.Add(idx);
            }
            return list;
        }

        // 返回每个bin的起止索引元组
        private static List<(int,int)> BinRanges(int start, int end, int bins)
        {
            bins = Math.Max(2, bins);
            int len = Math.Max(1, end - start + 1);
            var ranges = new List<(int,int)>(bins);
            for (int i = 0; i < bins; i++)
            {
                int i0 = start + (int)Math.Floor((double)i / bins * len);
                int i1 = start + (int)Math.Floor((double)(i + 1) / bins * len) - 1;
                i1 = Math.Max(i0, Math.Min(i1, end));
                ranges.Add((i0, i1));
            }
            return ranges;
        }

        // 简单移动平均，返回新数组，不修改原数据
        private static double[] Sma(double[] src, int window)
        {
            window = Math.Max(1, window);
            int n = src.Length;
            if (n == 0 || window <= 1) return src.ToArray();
            var dst = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += src[i];
                if (i >= window) sum -= src[i - window];
                dst[i] = sum / Math.Min(i + 1, window);
            }
            return dst;
        }

        // 对窗口最大值进行平滑，以避免因数据轻微变化导致的视觉抖动
        private double SmoothMaxAbs(double estimate)
        {
            if (!_yAutoSmoothInit)
            {
                _yAutoSmoothMaxAbs = estimate;
                _yAutoSmoothInit = true;
                return _yAutoSmoothMaxAbs;
            }
            // 指数平滑：新的估计占 25%，历史占 75%
            double alpha = 0.25;
            _yAutoSmoothMaxAbs = Math.Max(1e-6, _yAutoSmoothMaxAbs * (1 - alpha) + estimate * alpha);
            return _yAutoSmoothMaxAbs < 1e-6 ? 1.0 : _yAutoSmoothMaxAbs;
        }

        // 单通道：全局最大值（非自适配时使用）
        private static double EstimateGlobalMaxAbsYSingle(IReadOnlyList<CurvePoint> data)
        {
            double m = 0.0;
            for (int i = 0; i < data.Count; i++) m = Math.Max(m, Math.Abs(data[i].Y));
            return m < 1e-6 ? 1.0 : m;
        }

        // 单通道：窗口最大值（限采样数以降低卡顿）
        private double EstimateWindowMaxAbsYSingle(IReadOnlyList<CurvePoint> data, int start, int count, float usableWidth)
        {
            if (count == _yAutoLastCount && start == _yAutoLastStart && (DateTime.Now - _yAutoLastTime).TotalMilliseconds < 100)
                return _yAutoLastMaxAbs;
            int bins = Math.Min(128, Math.Max(1, (int)Math.Floor(usableWidth)));
            double spp = (double)count / bins;
            int lastIdx = Math.Min(start + count - 1, data.Count - 1);
            double max = 0.0;
            for (int ix = 0; ix < bins; ix++)
            {
                int si = Math.Min(start + (int)Math.Round(ix * spp), lastIdx);
                max = Math.Max(max, Math.Abs(data[si].Y));
            }
            if (max < 1e-6) max = 1.0;
            _yAutoLastStart = start; _yAutoLastCount = count; _yAutoLastMaxAbs = max; _yAutoLastTime = DateTime.Now;
            return max;
        }

        // 多通道：窗口最大值（限采样数以降低卡顿）
        private double EstimateWindowMaxAbsYMulti(Dictionary<int, IReadOnlyList<CurvePoint>> multi, List<int> ordered, int start, int count, float usableWidth)
        {
            if (count == _yAutoLastCount && start == _yAutoLastStart && (DateTime.Now - _yAutoLastTime).TotalMilliseconds < 100)
                return _yAutoLastMaxAbs;
            int bins = Math.Min(128, Math.Max(1, (int)Math.Floor(usableWidth)));
            double spp = (double)count / bins;
            double max = 0.0;
            foreach (var id in ordered)
            {
                var d = multi[id]; if (d == null || d.Count < 2) continue;
                int lastIdx = Math.Min(start + count - 1, d.Count - 1);
                for (int ix = 0; ix < bins; ix++)
                {
                    int si = Math.Min(start + (int)Math.Round(ix * spp), lastIdx);
                    max = Math.Max(max, Math.Abs(d[si].Y));
                }
            }
            if (max < 1e-6) max = 1.0;
            _yAutoLastStart = start; _yAutoLastCount = count; _yAutoLastMaxAbs = max; _yAutoLastTime = DateTime.Now;
            return max;
        }
    }
}
