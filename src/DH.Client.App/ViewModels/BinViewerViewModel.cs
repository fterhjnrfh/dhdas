using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Client.App.Services.Storage;
using DH.Contracts.Models;

namespace DH.Client.App.ViewModels;

public sealed class BinViewerViewModel : ObservableObject
{
    public IReadOnlyList<CurvePoint> CurrentCurveData { get; private set; } = Array.Empty<CurvePoint>();

    public Dictionary<int, IReadOnlyList<CurvePoint>> CurrentMultiChannelData { get; private set; } = new();

    public event Action? CurveDataUpdated;

    public Dictionary<int, Color> ChannelColorsMap { get; private set; } = new();

    public ObservableCollection<string> Devices { get; } = new();

    public ObservableCollection<BinChannelSelectionItem> ChannelSelectionItems { get; } = new();

    private Dictionary<string, List<SdkRawChannelDescriptor>> _deviceChannels = new(StringComparer.OrdinalIgnoreCase);

    public sealed class ViewState
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

    private string _statusMessage = "请选择 BIN 原始文件";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

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

    public IRelayCommand PlotSelectedChannelsCommand { get; }

    public IRelayCommand SelectAllChannelsCommand { get; }

    public IRelayCommand DeselectAllChannelsCommand { get; }

    public int DeviceFilterId { get; set; } = -1;

    public BinViewerViewModel()
    {
        PlotSelectedChannelsCommand = new RelayCommand(PlotSelectedChannels, CanPlotSelectedChannels);
        SelectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var item in ChannelSelectionItems)
            {
                item.IsSelected = true;
            }
        }, () => ChannelSelectionItems.Count > 0);
        DeselectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var item in ChannelSelectionItems)
            {
                item.IsSelected = false;
            }
        }, () => ChannelSelectionItems.Count > 0);
    }

    private void OnSelectedFileChanged()
    {
        Devices.Clear();
        ChannelSelectionItems.Clear();
        ChannelColorsMap.Clear();
        _deviceChannels.Clear();
        CurrentCurveData = Array.Empty<CurvePoint>();
        CurrentMultiChannelData = new();
        HasPlottedData = false;

        OnPropertyChanged(nameof(HasDevices));

        if (string.IsNullOrWhiteSpace(_selectedFile)
            || !File.Exists(_selectedFile)
            || !SdkRawCaptureFormat.IsRawCaptureFile(_selectedFile))
        {
            StatusMessage = "请选择 .sdkraw.bin 文件";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }

        StatusMessage = "正在读取 BIN 索引...";

        try
        {
            var dict = SdkRawCaptureReaderUtil.ListDevicesAndChannels(_selectedFile);
            if (dict.Count == 0)
            {
                StatusMessage = "当前 BIN 文件中没有可查看的通道";
                return;
            }

            _deviceChannels = dict.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);

            foreach (string device in dict.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                Devices.Add(device);
            }

            OnPropertyChanged(nameof(HasDevices));

            string? preferredDevice = Devices.FirstOrDefault(device => ParseDeviceIdFromDeviceName(device) == DeviceFilterId);
            SelectedDevice = preferredDevice ?? Devices.FirstOrDefault();

            int totalChannels = dict.Values.Sum(list => list.Count);
            StatusMessage = $"已加载 {totalChannels} 个 BIN 通道，请选择设备和通道";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取 BIN 失败: {ex.Message}";
        }

        (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OnSelectedDeviceChanged()
    {
        ChannelSelectionItems.Clear();

        if (string.IsNullOrWhiteSpace(_selectedDevice) || !_deviceChannels.TryGetValue(_selectedDevice, out var channels))
        {
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }

        int colorIndex = 0;
        foreach (var descriptor in channels
            .OrderBy(item => item.ChannelId)
            .ThenBy(item => item.ChannelName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new BinChannelSelectionItem(descriptor, GetColorByIndex(colorIndex++));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BinChannelSelectionItem.IsSelected))
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
        int selectedCount = ChannelSelectionItems.Count(item => item.IsSelected);
        int totalCount = ChannelSelectionItems.Count;
        StatusMessage = $"已选择 {selectedCount}/{totalCount} 个通道";
    }

    private bool CanPlotSelectedChannels()
        => !string.IsNullOrWhiteSpace(_selectedFile)
           && SdkRawCaptureFormat.IsRawCaptureFile(_selectedFile)
           && ChannelSelectionItems.Any(item => item.IsSelected);

    private void PlotSelectedChannels()
    {
        if (!CanPlotSelectedChannels())
        {
            return;
        }

        string filePath = _selectedFile!;
        var selectedItems = ChannelSelectionItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            StatusMessage = "请至少选择一个通道";
            return;
        }

        StatusMessage = $"正在绘制 {selectedItems.Count} 个 BIN 通道...";

        Task.Run(() =>
        {
            try
            {
                var channelIds = selectedItems.Select(item => item.ChannelId).ToArray();
                var curves = SdkRawCaptureReaderUtil.ReadChannelCurves(filePath, channelIds);
                var colors = selectedItems.ToDictionary(item => item.ChannelId, item => item.Color);

                Dispatcher.UIThread.Post(() =>
                {
                    ChannelColorsMap = colors;
                    CurrentMultiChannelData = curves;
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = curves.Count > 0;
                    StatusMessage = $"已绘制 {curves.Count} 个 BIN 通道（来自 {Path.GetFileName(filePath)}）";
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
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return -1;
        }

        var match = System.Text.RegularExpressions.Regex.Match(deviceName, @"设备\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int deviceId)
            ? deviceId
            : -1;
    }

    private static Color GetColorByIndex(int index)
    {
        var palette = new[]
        {
            Color.Parse("#D62728"),
            Color.Parse("#4169E1"),
            Color.Parse("#2CA02C"),
            Color.Parse("#FF8C00"),
            Color.Parse("#9467BD"),
            Color.Parse("#8B4513"),
            Color.Parse("#E377C2"),
            Color.Parse("#7F7F7F"),
            Color.Parse("#BCBD22"),
            Color.Parse("#17BECF"),
            Color.Parse("#FF6347"),
            Color.Parse("#4682B4"),
            Color.Parse("#32CD32"),
            Color.Parse("#FF69B4"),
            Color.Parse("#CD5C5C"),
            Color.Parse("#6A5ACD")
        };

        return palette[index % palette.Length];
    }
}

public sealed class BinChannelSelectionItem : ObservableObject
{
    public string DeviceDisplayName { get; }

    public string ChannelName { get; }

    public string FilePath { get; }

    public int ChannelId { get; }

    public long SampleCount { get; }

    public double SampleRateHz { get; }

    public Color Color { get; }

    public IBrush ColorBrush { get; }

    public string DisplayName => ChannelName;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public BinChannelSelectionItem(SdkRawChannelDescriptor descriptor, Color color)
    {
        DeviceDisplayName = descriptor.DeviceDisplayName;
        ChannelName = descriptor.ChannelName;
        FilePath = descriptor.FilePath;
        ChannelId = descriptor.ChannelId;
        SampleCount = descriptor.SampleCount;
        SampleRateHz = descriptor.SampleRateHz;
        Color = color;
        ColorBrush = new SolidColorBrush(color);
    }
}
