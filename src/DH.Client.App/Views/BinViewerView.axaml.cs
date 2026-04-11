using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DH.Client.App.Controls;
using DH.Client.App.Services.Storage;

namespace DH.Client.App.Views;

public partial class BinViewerView : UserControl
{
    public static readonly StyledProperty<string?> SelectedFileProperty =
        AvaloniaProperty.Register<BinViewerView, string?>(nameof(SelectedFile));

    public static readonly StyledProperty<int> SelectedDeviceIdProperty =
        AvaloniaProperty.Register<BinViewerView, int>(nameof(SelectedDeviceId), -1);

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

    public BinViewerView()
    {
        InitializeComponent();
        var vm = new ViewModels.BinViewerViewModel();
        DataContext = vm;

        var skView = this.FindControl<SkiaMultiChannelView>("BinCurveView");
        if (skView is not null)
        {
            skView.UseTimeAxis = false;
            skView.UseDataXValues = true;
            skView.FormatXLabel = FormatSecondsLabel;
            skView.ScrollMode = false;
            skView.DesiredXTicks = 10;
            skView.DesiredYTicks = 8;
            skView.ShowLegend = true;
            skView.UseExtremaAggregation = true;

            skView.DataProvider = () => vm.CurrentCurveData;
            skView.MultiChannelDataProvider = () => vm.CurrentMultiChannelData;
            skView.ChannelColorsMap = vm.ChannelColorsMap;
            vm.CurveDataUpdated += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                skView.ChannelColorsMap = vm.ChannelColorsMap;
                skView.InvalidateVisual();
            });

            skView.ViewStateChanged += state =>
            {
                vm.LastView = new ViewModels.BinViewerViewModel.ViewState
                {
                    ZoomX = state.ZoomX,
                    ViewLeft = state.ViewLeft,
                    ViewCount = state.ViewCount
                };
            };

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

        this.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectedFileProperty)
            {
                vm.SelectedFile = e.NewValue as string;
            }
            else if (e.Property == SelectedDeviceIdProperty)
            {
                vm.DeviceFilterId = (int)(e.NewValue ?? -1);
            }
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
        var skView = this.FindControl<SkiaMultiChannelView>("BinCurveView");
        skView?.ResetView();
    }

    private void OnJumpToEndClicked(object? sender, RoutedEventArgs e)
    {
        var skView = this.FindControl<SkiaMultiChannelView>("BinCurveView");
        if (skView is null || DataContext is not ViewModels.BinViewerViewModel vm)
        {
            return;
        }

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
            if (!running)
            {
                vm.JumpProgress = 100;
            }
        }

        void OnJumpProgressChanged(double progress)
        {
            vm.JumpProgress = (int)Math.Clamp(Math.Round(progress), 0, 100);
        }
    }

    private async void OnPickFileClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 BIN 原始文件",
            AllowMultiple = false,
            Filters = new List<FileDialogFilter>
            {
                new() { Name = "BIN 原始文件", Extensions = new List<string> { "bin" } },
                new() { Name = "所有文件", Extensions = new List<string> { "*" } }
            }
        };

        try
        {
            var initialDir = TryGetDataDir();
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            {
                dialog.Directory = initialDir;
            }
        }
        catch
        {
        }

        if (this.VisualRoot is not Window window)
        {
            return;
        }

        var result = await dialog.ShowAsync(window);
        var filePath = result?.Length > 0 ? result[0] : null;
        if (!string.IsNullOrWhiteSpace(filePath)
            && File.Exists(filePath)
            && SdkRawCaptureFormat.IsRawCaptureFile(filePath))
        {
            SelectedFile = filePath;
        }
    }

    private static string? TryGetDataDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Directory.GetCurrentDirectory(), "data"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data")),
            "D:/DH2/data",
            "D:/DHDAS/data"
        };

        foreach (string path in candidates)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
