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

public class Hdf5ViewerViewModel : ObservableObject
{
    public IReadOnlyList<CurvePoint> CurrentCurveData { get; private set; } = Array.Empty<CurvePoint>();

    public Dictionary<int, IReadOnlyList<CurvePoint>> CurrentMultiChannelData { get; private set; } = new();

    public event Action? CurveDataUpdated;

    public Dictionary<int, Color> ChannelColorsMap { get; private set; } = new();

    public ObservableCollection<string> Devices { get; } = new();

    public ObservableCollection<Hdf5ChannelSelectionItem> ChannelSelectionItems { get; } = new();

    private Dictionary<string, List<Hdf5ChannelDescriptor>> _deviceChannels = new(StringComparer.OrdinalIgnoreCase);

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

    private string _statusMessage = "请选择HDF5文件";
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

    public ObservableCollection<string> Channels { get; } = new();

    private string? _selectedChannel;
    public string? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    public IRelayCommand PlotSelectedChannelsCommand { get; }

    public IRelayCommand SelectAllChannelsCommand { get; }

    public IRelayCommand DeselectAllChannelsCommand { get; }

    public int DeviceFilterId { get; set; } = -1;

    public Hdf5ViewerViewModel()
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
        Channels.Clear();
        ChannelColorsMap.Clear();
        _deviceChannels.Clear();
        CurrentMultiChannelData = new();
        CurrentCurveData = Array.Empty<CurvePoint>();
        HasPlottedData = false;

        OnPropertyChanged(nameof(HasDevices));

        if (string.IsNullOrWhiteSpace(_selectedFile) || !File.Exists(_selectedFile))
        {
            StatusMessage = "请选择HDF5文件";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }

        StatusMessage = "正在读取HDF5会话...";

        try
        {
            var dict = Hdf5ReaderUtil.ListDevicesAndChannels(_selectedFile);
            if (dict.Count == 0)
            {
                StatusMessage = "文件中没有找到HDF5通道";
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
            StatusMessage = $"已加载 {totalChannels} 个HDF5通道，请选择设备和通道";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取HDF5失败: {ex.Message}";
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
            var item = new Hdf5ChannelSelectionItem(descriptor, GetColorByIndex(colorIndex++));
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Hdf5ChannelSelectionItem.IsSelected))
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
        => !string.IsNullOrWhiteSpace(_selectedFile) && ChannelSelectionItems.Any(item => item.IsSelected);

    private void PlotSelectedChannels()
    {
        if (!CanPlotSelectedChannels())
        {
            return;
        }

        string sessionFile = _selectedFile!;
        var selectedItems = ChannelSelectionItems.Where(item => item.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            StatusMessage = "请至少选择一个通道";
            return;
        }

        StatusMessage = $"正在绘制 {selectedItems.Count} 个HDF5通道...";

        Task.Run(() =>
        {
            try
            {
                var dataMap = new Dictionary<int, IReadOnlyList<CurvePoint>>();
                var colorMap = new Dictionary<int, Color>();
                const int MaxPoints = 100_000;

                foreach (var item in selectedItems)
                {
                    try
                    {
                        var y = Hdf5ReaderUtil.ReadChannelData(item.FilePath);
                        var props = Hdf5ReaderUtil.ReadChannelProperties(item.FilePath);
                        double increment = TryGetDouble(props, "wf_increment") ?? 1.0;
                        double offset = TryGetDouble(props, "wf_start_offset") ?? 0.0;
                        if (increment <= 0d)
                        {
                            increment = 1.0;
                        }

                        double[] x;
                        if (y.Length > MaxPoints)
                        {
                            int stride = (int)Math.Ceiling((double)y.Length / MaxPoints);
                            int length = (int)Math.Ceiling((double)y.Length / stride);
                            var downsampled = new double[length];
                            x = new double[length];

                            for (int i = 0, j = 0; i < y.Length; i += stride, j++)
                            {
                                downsampled[j] = y[i];
                                x[j] = offset + (i * increment);
                            }

                            y = downsampled;
                        }
                        else
                        {
                            x = new double[y.Length];
                            for (int i = 0; i < y.Length; i++)
                            {
                                x[i] = offset + (i * increment);
                            }
                        }

                        var points = new List<CurvePoint>(y.Length);
                        for (int i = 0; i < y.Length; i++)
                        {
                            points.Add(new CurvePoint(x[i], y[i]));
                        }

                        dataMap[item.ChannelId] = points;
                        colorMap[item.ChannelId] = item.Color;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[HDF5] 读取通道 {item.ChannelName} 失败: {ex.Message}");
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    ChannelColorsMap = colorMap;
                    CurrentMultiChannelData = dataMap;
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = dataMap.Count > 0;
                    StatusMessage = $"已绘制 {dataMap.Count} 个HDF5通道（来自 {Path.GetFileName(sessionFile)}）";
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

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out double parsed) => parsed,
            _ => null
        };
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

public sealed class Hdf5ChannelSelectionItem : ObservableObject
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

    public Hdf5ChannelSelectionItem(Hdf5ChannelDescriptor descriptor, Color color)
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
