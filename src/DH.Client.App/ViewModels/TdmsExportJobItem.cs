using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DH.Client.App.Services.Storage;

namespace DH.Client.App.ViewModels;

public partial class TdmsExportJobItem : ObservableObject
{
    public Guid JobId { get; } = Guid.NewGuid();

    public string CapturePath { get; }

    public bool PerChannel { get; }

    public CompressionType CompressionType { get; }

    public PreprocessType PreprocessType { get; }

    public CompressionOptions CompressionOptions { get; }

    public IReadOnlyCollection<int>? SelectedChannelIds { get; }

    public DateTime RequestedAtLocal { get; } = DateTime.Now;

    public string SourceFileName => Path.GetFileName(CapturePath);

    public string ExportModeText => PerChannel ? "每通道TDMS" : "单文件TDMS";

    public string SelectionText { get; }

    public string TitleText => $"{SourceFileName} -> {ExportModeText}";

    public string ConfigSummaryText => $"范围: {SelectionText} | 算法: {CompressionType} | 预处理: {PreprocessType}";

    [ObservableProperty] private string _stateText = "排队中";
    [ObservableProperty] private string _progressText = "等待开始";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private DateTime? _startedAtLocal;
    [ObservableProperty] private DateTime? _completedAtLocal;

    public TdmsExportJobItem(
        string capturePath,
        bool perChannel,
        CompressionType compressionType,
        PreprocessType preprocessType,
        CompressionOptions compressionOptions,
        IReadOnlyCollection<int>? selectedChannelIds,
        string selectionText)
    {
        CapturePath = capturePath;
        PerChannel = perChannel;
        CompressionType = compressionType;
        PreprocessType = preprocessType;
        CompressionOptions = compressionOptions;
        SelectedChannelIds = selectedChannelIds;
        SelectionText = selectionText;
    }

    public string RequestedAtText => RequestedAtLocal.ToString("yyyy-MM-dd HH:mm:ss");
}
