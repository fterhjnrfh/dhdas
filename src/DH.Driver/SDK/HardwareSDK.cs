// DH.Driver/SDK/HardwareSDK.cs
// 东华SDK接口封装 - 从Demo_C#项目移植
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DH.Driver.SDK;

/// <summary>
/// 东华硬件SDK接口封装类
/// 通过P/Invoke调用Hardware_Standard_C_Interface.dll
/// </summary>
public static class HardwareSDK
{
    public const int StandardCapacity = 204800;
    private const string LibName = "Hardware_Standard_C_Interface.dll";
    private static readonly object NativeLoadSync = new();
    private static IntPtr s_nativeHandle;
    private static bool s_nativeLoadAttempted;
    private static string? s_nativeLoadError;

    static HardwareSDK()
    {
        NativeLibrary.SetDllImportResolver(typeof(HardwareSDK).Assembly, ResolveSdkNativeLibrary);
    }

    public static string? NativeLoadDiagnostics => s_nativeLoadError;

    private static IntPtr ResolveSdkNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        EnsureSdkNativeLibraryLoaded();
        return s_nativeHandle;
    }

    private static void EnsureSdkNativeLibraryLoaded()
    {
        lock (NativeLoadSync)
        {
            if (s_nativeLoadAttempted)
            {
                if (s_nativeHandle == IntPtr.Zero && !string.IsNullOrWhiteSpace(s_nativeLoadError))
                {
                    throw new DllNotFoundException(s_nativeLoadError);
                }

                return;
            }

            s_nativeLoadAttempted = true;

            foreach (string dir in EnumerateSdkRuntimeDirectories())
            {
                try
                {
                    PrepareNativeSearchPath(dir);

                    string dllPath = Path.Combine(dir, LibName);
                    if (!File.Exists(dllPath))
                    {
                        continue;
                    }

                    if (NativeLibrary.TryLoad(dllPath, out s_nativeHandle))
                    {
                        s_nativeLoadError = null;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    s_nativeLoadError = $"Unable to load {LibName} from '{dir}': {ex.Message}";
                }
            }

            s_nativeLoadError ??= $"Unable to locate {LibName}. Checked AppContext.BaseDirectory and repository SDK output folders.";
            throw new DllNotFoundException(s_nativeLoadError);
        }
    }

    private static IEnumerable<string> EnumerateSdkRuntimeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (Directory.Exists(fullPath) && seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        Add(AppContext.BaseDirectory);
        Add(Path.GetDirectoryName(typeof(HardwareSDK).Assembly.Location));

        string? repoRoot = TryFindRepositoryRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            Add(Path.Combine(repoRoot, "bin", "Debug", "net6.0-windows7.0"));
            Add(Path.Combine(repoRoot, "src", "DH.UI", "bin", "Debug", "net6.0-windows7.0"));
        }

        return result;
    }

    private static string? TryFindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            string appHostProject = Path.Combine(current.FullName, "DH.AppHost.csproj");
            string srcDirectory = Path.Combine(current.FullName, "src");
            if (File.Exists(appHostProject) && Directory.Exists(srcDirectory))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void PrepareNativeSearchPath(string directory)
    {
        PrependProcessPath(directory);

        string bm1302Dir = Path.Combine(directory, "BM1302");
        if (Directory.Exists(bm1302Dir))
        {
            PrependProcessPath(bm1302Dir);
        }
    }

    private static void PrependProcessPath(string directory)
    {
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string prefix = directory + Path.PathSeparator;
        if (currentPath.Contains(prefix, StringComparison.OrdinalIgnoreCase) ||
            currentPath.Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Environment.SetEnvironmentVariable("PATH", prefix + currentPath);
    }

    #region 回调委托定义

    /// <summary>
    /// 采样数据回调委托
    /// </summary>
    /// <param name="sampleTime">采样时间</param>
    /// <param name="groupIdSize">组ID大小</param>
    /// <param name="groupInfo">组信息指针</param>
    /// <param name="nMessageType">消息类型</param>
    /// <param name="nGroupID">组ID</param>
    /// <param name="nChannelStyle">通道类型</param>
    /// <param name="nChannelID">通道ID</param>
    /// <param name="nMachineID">机器ID</param>
    /// <param name="nTotalDataCount">总数据数量</param>
    /// <param name="nDataCountPerChannel">每通道数据数量</param>
    /// <param name="nBufferCount">缓冲区大小(字节)</param>
    /// <param name="nBlockIndex">块索引</param>
    /// <param name="varSampleData">采样数据指针</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate void SampleDataChangeEventHandle(
        long sampleTime, 
        int groupIdSize, 
        IntPtr groupInfo,
        int nMessageType, 
        int nGroupID, 
        int nChannelStyle,
        int nChannelID, 
        int nMachineID, 
        long nTotalDataCount,
        int nDataCountPerChannel, 
        int nBufferCount, 
        int nBlockIndex,
        long varSampleData);

    #endregion

    #region SDK初始化与退出

    /// <summary>
    /// 初始化SDK控制
    /// </summary>
    /// <param name="dll_dir">配置文件目录路径</param>
    /// <returns>0成功，负数失败</returns>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int InitMacControl(string dll_dir);

    /// <summary>
    /// 退出SDK控制，释放资源
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void QuitMacControl();

    /// <summary>
    /// 设置数据变化回调函数
    /// </summary>
    /// <param name="callback">回调函数委托</param>
    /// <returns>0成功</returns>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int SetDataChangeCallBackFun(SampleDataChangeEventHandle callback);

    /// <summary>
    /// 释放数据缓冲区
    /// </summary>
    /// <param name="point">缓冲区指针</param>
    /// <returns>0成功</returns>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int DA_ReleaseBuffer(long point);

    #endregion

    #region 设备连接管理

    /// <summary>
    /// 重新查找并连接设备
    /// </summary>
    /// <returns>是否成功</returns>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool RefindAndConnecMac();

    /// <summary>
    /// 获取在线设备数量
    /// </summary>
    /// <returns>在线设备数量</returns>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetAllMacOnlineCount();

    /// <summary>
    /// 根据索引获取设备信息
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetMacInfoFromIndex(int nIndex, out int pMacID, IntPtr strMacIp, int nMacBuffer, out int nUseBuffer);

    /// <summary>
    /// 获取设备当前通道数量
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetMacCurrentChnCount(int nMachineID, string strMacIp);

    /// <summary>
    /// 获取设备连接状态
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern byte GetMacLinkStatus(int nMachineID, string strMacIp);

    #endregion

    #region 采样控制

    /// <summary>
    /// 启动采样
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void StartMacSample();

    /// <summary>
    /// 停止采样
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void StopMacSample();

    /// <summary>
    /// 获取采样频率列表
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool GetMacSampleFreqList(IntPtr pFreqList, int nFreqBuffer, out int nUsedBuffer);

    /// <summary>
    /// 获取当前采样频率
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern float GetMacCurrentSampleFreq();

    /// <summary>
    /// 设置采样频率
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool SetMacSampleFreq(float fltSampleFreq);

    /// <summary>
    /// 设置每次获取的数据量
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int SetGetDataCountEveryTime(int nDataCount);

    #endregion

    #region 通道操作

    /// <summary>
    /// 全通道平衡
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int AllChannelBalance();

    /// <summary>
    /// 全通道清零
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int AllChannelClearZeroEx(int nGND);

    /// <summary>
    /// 获取通道测量类型
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetChannelMeasureType(int nMachineID, string strMachineIP, int nChannelID, out int nMeasureType);

    /// <summary>
    /// 根据通道索引获取通道ID
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int GetChannelIDFromAllChannelIndex(int nMachineID, string pMacIp, int nIndex, out int nMacChnId, out int bOnLine);

    #endregion

    #region 参数加载保存

    /// <summary>
    /// 加载参数
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int LoadMacParameter(string pFilePath);

    /// <summary>
    /// 保存参数
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int SaveMacParameter(string pFilePath);

    #endregion
}

/// <summary>
/// SDK消息类型常量 - 来自Demo_C#的SampleData.cs
/// </summary>
public static class SdkMessageTypes
{
    /// <summary>
    /// 所有仪器的模拟通道的数据
    /// </summary>
    public const int SAMPLE_ANALOG_DATA = 0;
    
    /// <summary>
    /// 单台仪器的数据
    /// </summary>
    public const int SAMPLE_SINGLEGROUP_ANALOGDATA = 5;
    
    /// <summary>
    /// 多通道数据（抽点后的数据）
    /// </summary>
    public const int SAMPLE_ANALOG_MULTIFREQCHN_DATA = 20;
    
    /// <summary>
    /// 多通道数据(未抽点数据)
    /// </summary>
    public const int SAMPLE_ANALOG_MULTICHN_DATA = 21;
}
