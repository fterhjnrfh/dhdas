using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Client.App.Services.Storage;
using DH.Contracts.Models;
using Avalonia.Media;
using Avalonia.Threading;

namespace DH.Client.App.ViewModels;

public class TdmsViewerViewModel : ObservableObject
{
    // 当前曲线数据（单通道）
    public IReadOnlyList<CurvePoint> CurrentCurveData { get; private set; } = Array.Empty<CurvePoint>();
    // 当前多通道数据
    public Dictionary<int, IReadOnlyList<CurvePoint>> CurrentMultiChannelData { get; private set; } = new();
    // 数据更新事件（供视图刷新）
    public event Action? CurveDataUpdated;

    // 通道颜色映射
    public Dictionary<int, Color> ChannelColorsMap { get; private set; } = new();
    
    // 设备列表
    public ObservableCollection<string> Devices { get; } = new();
    
    // 通道选择项（多选）
    public ObservableCollection<ChannelSelectionItem> ChannelSelectionItems { get; } = new();
    
    // 内部存储：组名->通道名列表
    private Dictionary<string, List<string>> _groupChannels = new();
    // 设备->组名映射
    private Dictionary<string, string> _deviceGroupMap = new();

    // 记录用户最后一次视图状态（缩放与窗口）
    public class ViewState
    {
        public float ZoomX { get; set; }
        public int ViewLeft { get; set; }
        public int ViewCount { get; set; }
    }
    public ViewState? LastView { get; set; }

    private string? _selectedFile;
    public string? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                OnSelectedFileChanged();
            }
        }
    }
    
    private string? _selectedDevice;
    public string? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnSelectedDeviceChanged();
            }
        }
    }
    
    public bool HasDevices => Devices.Count > 0;
    
    private bool _hasPlottedData;
    public bool HasPlottedData
    {
        get => _hasPlottedData;
        set => SetProperty(ref _hasPlottedData, value);
    }
    
    private string _statusMessage = "请选择TDMS文件";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // 跳转状态与进度
    private bool _isJumping;
    public bool IsJumping
    {
        get => _isJumping;
        set => SetProperty(ref _isJumping, value);
    }
    private int _jumpProgress;
    public int JumpProgress
    {
        get => _jumpProgress;
        set => SetProperty(ref _jumpProgress, value);
    }

    // 旧的通道列表（兼容保留）
    public ObservableCollection<string> Channels { get; } = new();
    private string? _selectedChannel;
    public string? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    // 命令
    public IRelayCommand PlotSelectedChannelsCommand { get; }
    public IRelayCommand SelectAllChannelsCommand { get; }
    public IRelayCommand DeselectAllChannelsCommand { get; }

    public TdmsViewerViewModel()
    {
        PlotSelectedChannelsCommand = new RelayCommand(PlotSelectedChannels, CanPlotSelectedChannels);

        SelectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var it in ChannelSelectionItems) it.IsSelected = true;
        }, () => ChannelSelectionItems.Count > 0);

        DeselectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var it in ChannelSelectionItems) it.IsSelected = false;
        }, () => ChannelSelectionItems.Count > 0);
    }

    // 设备过滤ID
    public int DeviceFilterId { get; set; } = 0;
    // 在线通道管理器
    public DH.Client.App.Data.OnlineChannelManager? OnlineChannelManager { get; set; }

    private void OnSelectedFileChanged()
    {
        // 清空所有状态
        Devices.Clear();
        ChannelSelectionItems.Clear();
        Channels.Clear();
        ChannelColorsMap.Clear();
        _groupChannels.Clear();
        _deviceGroupMap.Clear();
        CurrentMultiChannelData = new();
        CurrentCurveData = Array.Empty<CurvePoint>();
        HasPlottedData = false;
        
        OnPropertyChanged(nameof(HasDevices));
        
        if (string.IsNullOrWhiteSpace(_selectedFile) || !File.Exists(_selectedFile))
        {
            StatusMessage = "请选择TDMS文件";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }
        
        StatusMessage = "正在读取文件...";
        
        // 读取组和通道
        var dict = TdmsReaderUtil.ListGroupsAndChannels(_selectedFile);
        if (dict.Count == 0)
        {
            StatusMessage = "文件中没有找到数据通道";
            return;
        }
        
        _groupChannels = dict.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        
        // 解析设备列表
        var deviceSet = new HashSet<string>();
        foreach (var groupName in dict.Keys)
        {
            foreach (var channelName in dict[groupName])
            {
                var deviceId = DH.Contracts.ChannelNaming.ParseDeviceId(channelName);
                // 支持设备ID从0开始（SDK模式设备号可能为0、1、2...）
                if (deviceId >= 0)
                {
                    var deviceName = $"设备 {deviceId} ({DH.Contracts.ChannelNaming.DeviceDisplayName(deviceId)})";
                    if (deviceSet.Add(deviceName))
                    {
                        _deviceGroupMap[deviceName] = groupName;
                    }
                }
            }
            
            // 如果没有解析出设备，使用组名作为设备
            if (deviceSet.Count == 0)
            {
                var deviceName = $"组: {groupName}";
                deviceSet.Add(deviceName);
                _deviceGroupMap[deviceName] = groupName;
            }
        }
        
        // 如果还是没有设备，创建一个默认设备
        if (deviceSet.Count == 0)
        {
            var defaultDevice = "默认设备";
            deviceSet.Add(defaultDevice);
            if (_groupChannels.Count > 0)
            {
                _deviceGroupMap[defaultDevice] = _groupChannels.Keys.First();
            }
        }
        
        foreach (var device in deviceSet.OrderBy(d => d))
        {
            Devices.Add(device);
        }
        
        OnPropertyChanged(nameof(HasDevices));
        
        // 自动选择第一个设备
        if (Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
        }
        
        StatusMessage = $"已加载 {dict.Values.Sum(v => v.Length)} 个通道，请选择设备和通道";
        (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    
    private void OnSelectedDeviceChanged()
    {
        ChannelSelectionItems.Clear();
        
        if (string.IsNullOrWhiteSpace(_selectedDevice))
        {
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }
        
        // 获取该设备对应的组
        if (!_deviceGroupMap.TryGetValue(_selectedDevice, out var groupName))
        {
            groupName = _groupChannels.Keys.FirstOrDefault() ?? "";
        }
        
        if (string.IsNullOrEmpty(groupName) || !_groupChannels.ContainsKey(groupName))
        {
            StatusMessage = "未找到该设备的通道";
            return;
        }
        
        var channels = _groupChannels[groupName];
        
        // 根据设备筛选通道（支持设备ID从0开始）
        var deviceId = ParseDeviceIdFromDeviceName(_selectedDevice);
        var filteredChannels = deviceId >= 0
            ? channels.Where(c => DH.Contracts.ChannelNaming.ParseDeviceId(c) == deviceId).ToList()
            : channels.ToList();
        
        // 如果筛选后没有通道，使用所有通道
        if (filteredChannels.Count == 0)
        {
            filteredChannels = channels.ToList();
        }
        
        int colorIndex = 0;
        foreach (var channelName in filteredChannels.OrderBy(c => DH.Contracts.ChannelNaming.ParseChannelName(c)))
        {
            var channelId = DH.Contracts.ChannelNaming.ParseChannelName(channelName);
            var color = GetColorByIndex(colorIndex++);
            var item = new ChannelSelectionItem(groupName, channelName, channelId, color);
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ChannelSelectionItem.IsSelected))
                {
                    (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
                    UpdateStatusMessage();
                }
            };
            ChannelSelectionItems.Add(item);
        }
        
        UpdateStatusMessage();
        (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    
    private void UpdateStatusMessage()
    {
        var selectedCount = ChannelSelectionItems.Count(c => c.IsSelected);
        var totalCount = ChannelSelectionItems.Count;
        StatusMessage = $"已选择 {selectedCount}/{totalCount} 个通道";
    }

    private bool CanPlotSelectedChannels()
    {
        return !string.IsNullOrWhiteSpace(_selectedFile) 
               && ChannelSelectionItems.Any(c => c.IsSelected);
    }

    private void PlotSelectedChannels()
    {
        if (!CanPlotSelectedChannels()) return;
        
        var file = _selectedFile!;
        var selectedItems = ChannelSelectionItems.Where(c => c.IsSelected).ToList();
        
        if (selectedItems.Count == 0)
        {
            StatusMessage = "请至少选择一个通道";
            return;
        }
        
        StatusMessage = $"正在绘制 {selectedItems.Count} 个通道...";
        
        Task.Run(() =>
        {
            try
            {
                var tmpData = new Dictionary<int, IReadOnlyList<CurvePoint>>();
                var tmpColors = new Dictionary<int, Color>();
                const int MaxPoints = 100_000;
                
                foreach (var item in selectedItems)
                {
                    try
                    {
                        var y = TdmsReaderUtil.ReadChannelData(file, item.GroupName, item.ChannelName);
                        var props = TdmsReaderUtil.ReadChannelProperties(file, item.GroupName, item.ChannelName);
                        
                        double increment = TryGetDouble(props, "wf_increment") ?? 1.0;
                        double offset = TryGetDouble(props, "wf_start_offset") ?? 0.0;
                        
                        double[] x;
                        if (y.Length > MaxPoints)
                        {
                            int stride = (int)Math.Ceiling((double)y.Length / MaxPoints);
                            int n = (int)Math.Ceiling((double)y.Length / stride);
                            var y2 = new double[n];
                            x = new double[n];
                            for (int i = 0, j = 0; i < y.Length; i += stride, j++)
                            {
                                y2[j] = y[i];
                                x[j] = offset + i * increment;
                            }
                            y = y2;
                        }
                        else
                        {
                            x = new double[y.Length];
                            for (int i = 0; i < y.Length; i++) x[i] = offset + i * increment;
                        }
                        
                        var list = new List<CurvePoint>(y.Length);
                        for (int i = 0; i < y.Length; i++) list.Add(new CurvePoint(x[i], y[i]));
                        
                        tmpData[item.ChannelId] = list;
                        tmpColors[item.ChannelId] = item.Color;
                        
                        Console.WriteLine($"[TDMS] 读取通道 {item.ChannelName}, 数据点: {list.Count}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TDMS] 读取通道 {item.ChannelName} 失败: {ex.Message}");
                    }
                }
                
                Dispatcher.UIThread.Post(() =>
                {
                    ChannelColorsMap = tmpColors;
                    CurrentMultiChannelData = tmpData;
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = tmpData.Count > 0;
                    StatusMessage = $"已绘制 {tmpData.Count} 个通道";
                    CurveDataUpdated?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = $"绘制失败: {ex.Message}";
                    CurrentMultiChannelData = new();
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = false;
                    CurveDataUpdated?.Invoke();
                });
            }
        });
    }

    private static int ParseDeviceIdFromDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return -1;
        // 解析 "设备 X (AIxx)" 格式
        var match = System.Text.RegularExpressions.Regex.Match(deviceName, @"设备\s*(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            return id;  // 可以是0、1、2等
        return -1;  // -1表示未找到
    }

    private static int ParseChannelId(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        // 尝试解析 AI{设备号}-{通道号} 格式，如 AI0-01 -> 1, AI1-16 -> 116
        var match = System.Text.RegularExpressions.Regex.Match(name, @"AI(\d+)-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var dev) && int.TryParse(match.Groups[2].Value, out var ch))
                return dev * 100 + ch;
        }
        // 退化：提取所有数字
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var id)) return id;
        return -1;  // -1表示未找到
    }

    private static int ParseDeviceIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        try
        {
            // 尝试解析 AI{设备号}-{通道号} 格式，如 AI0-01, AI1-16
            int ai = name.IndexOf("AI", StringComparison.OrdinalIgnoreCase);
            if (ai >= 0 && ai + 2 < name.Length)
            {
                string s = name.Substring(ai + 2);
                // 提取设备号（直到遇到非数字字符）
                var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length >= 1 && int.TryParse(digits, out var dev)) return dev;
            }
            // 尝试从通道ID解析（格式：设备ID*100+通道号）
            int chId = ParseChannelId(name);
            if (chId >= 0) return chId / 100;  // 支持设备ID=0
            return -1;
        }
        catch { return -1; }
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            double d => d,
            float f => (double)f,
            int i => (double)i,
            long l => (double)l,
            string s when double.TryParse(s, out var d2) => d2,
            _ => null
        };
    }

    private static Color GetColorByIndex(int index)
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
            Color.Parse("#6A5ACD"), // 石板蓝
        };
        
        return palette[index % palette.Count];
    }
}

/// <summary>
/// 通道选择项（用于多选列表）
/// </summary>
public class ChannelSelectionItem : ObservableObject
{
    public string GroupName { get; }
    public string ChannelName { get; }
    public int ChannelId { get; }
    public Color Color { get; }
    public IBrush ColorBrush { get; }
    
    public string DisplayName => ChannelName;
    
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
    
    public ChannelSelectionItem(string groupName, string channelName, int channelId, Color color)
    {
        GroupName = groupName;
        ChannelName = channelName;
        // channelId >= 0 表示有效的通道ID（设备0的通道1的ID是1）
        // -1 表示解析失败，使用哈希值作为备用ID
        ChannelId = channelId >= 0 ? channelId : Math.Abs(channelName.GetHashCode());
        Color = color;
        ColorBrush = new SolidColorBrush(color);
    }
}
