using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace DH.Client.App.Services.Storage
{
    /// <summary>
    /// 存储断电保护守卫 —— 确保在进程异常终止、用户关闭窗口或断电等场景下
    /// 最大限度地保全已写入的 TDMS 数据。
    ///
    /// 工作原理：
    /// 1. 【进程退出钩子】注册 ProcessExit / CancelKeyPress / UnhandledException，在退出前
    ///    紧急执行 Flush + SaveFile，将内存缓冲和 TDMS 索引刷写到磁盘。
    /// 2. 【周期性刷盘】启动后台定时器，每隔一定间隔（默认 5 秒）自动执行一次
    ///    Flush → DDC_SaveFile，确保磁盘文件始终包含最新数据。即使突然断电，
    ///    也只丢失最多一个刷盘周期内的数据。
    /// 3. 【恢复日志】在存储目录写入 .guard 日志文件，记录写入会话状态和最后刷盘时间，
    ///    下次启动时可据此判断上次是否异常退出并提示用户。
    ///
    /// 使用方式：
    ///   在 StartStorageAsync 成功后调用 StorageGuard.Activate(storage, basePath)；
    ///   在 StopStorage 完成后调用 StorageGuard.Deactivate()。
    /// </summary>
    public static class StorageGuard
    {
        // ======================== 配置 ========================

        /// <summary>自动刷盘间隔（毫秒）</summary>
        private const int FlushIntervalMs = 1_000;

        /// <summary>恢复日志文件名</summary>
        private const string GuardFileName = ".storage_guard.json";

        // ======================== 状态 ========================

        private static ITdmsStorage? _storage;
        private static string? _basePath;
        private static string? _sessionName;
        private static DateTime _startTime;
        private static Timer? _flushTimer;
        private static volatile bool _active;
        private static readonly object _lock = new();
        private static string? _guardFilePath;
        private static DateTime _lastFlushUtc;
        private static long _flushCount;

        // ======================== 公共 API ========================

        /// <summary>
        /// 激活断电保护。应在存储成功 Start 之后调用。
        /// </summary>
        /// <param name="storage">当前活跃的存储实例</param>
        /// <param name="basePath">存储输出目录</param>
        /// <param name="sessionName">会话名（用于日志）</param>
        public static void Activate(ITdmsStorage storage, string basePath, string sessionName = "")
        {
            lock (_lock)
            {
                if (_active) return;

                _storage = storage ?? throw new ArgumentNullException(nameof(storage));
                _basePath = basePath;
                _sessionName = sessionName;
                _startTime = DateTime.Now;
                _flushCount = 0;
                _lastFlushUtc = DateTime.UtcNow;
                _active = true;

                // 写入守卫日志：标记写入中
                _guardFilePath = Path.Combine(basePath, GuardFileName);
                WriteGuardLog(sessionName, GuardState.Writing);

                // 注册进程退出钩子
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                // 启动周期性刷盘定时器
                _flushTimer = new Timer(OnFlushTimerTick, null, FlushIntervalMs, FlushIntervalMs);

                Console.WriteLine($"[StorageGuard] 已激活断电保护，刷盘间隔={FlushIntervalMs}ms，目录={basePath}");
            }
        }

        /// <summary>
        /// 停用断电保护。应在存储正常 Stop 之后调用。
        /// </summary>
        public static void Deactivate()
        {
            lock (_lock)
            {
                if (!_active) return;
                _active = false;

                // 停止定时器
                _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _flushTimer?.Dispose();
                _flushTimer = null;

                // 取消进程退出钩子
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                Console.CancelKeyPress -= OnCancelKeyPress;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

                // 更新守卫日志：标记正常完成
                WriteGuardLog("", GuardState.Completed);

                _storage = null;
                // 注意：_basePath/_sessionName/_startTime 保留，供 OrganizeToTimestampFolder 使用

                Console.WriteLine($"[StorageGuard] 已停用断电保护，总刷盘次数={_flushCount}");
            }
        }

        /// <summary>
        /// 将存储目录中本次写入生成的所有文件（.tdms, .tdms_index, .sha256, .storage_guard.json）
        /// 移动到以时间命名的子文件夹中。
        /// 返回新文件夹路径；如果无文件可移动则返回 null。
        /// </summary>
        /// <param name="writtenFiles">本次写入产生的 TDMS 文件路径列表</param>
        /// <returns>时间命名的子文件夹路径，以及更新后的文件路径列表</returns>
        public static (string? folderPath, List<string> newFilePaths) OrganizeToTimestampFolder(
            IReadOnlyList<string>? writtenFiles)
        {
            var newPaths = new List<string>();
            var basePath = _basePath;

            if (string.IsNullOrEmpty(basePath) || writtenFiles == null || writtenFiles.Count == 0)
                return (null, newPaths);

            try
            {
                // 以写入开始时间命名文件夹，格式：yyyy-MM-dd_HH-mm-ss
                var folderName = _startTime.ToString("yyyy-MM-dd_HH-mm-ss");
                
                // 如果有会话名，附加到文件夹名
                if (!string.IsNullOrWhiteSpace(_sessionName))
                {
                    var safeName = SanitizeFolderName(_sessionName);
                    if (!string.IsNullOrEmpty(safeName))
                        folderName = $"{folderName}_{safeName}";
                }

                var targetDir = Path.Combine(basePath, folderName);
                
                // 如果目标文件夹已存在（同一秒内重复操作），加序号
                if (Directory.Exists(targetDir))
                {
                    int seq = 1;
                    while (Directory.Exists($"{targetDir}_{seq}")) seq++;
                    targetDir = $"{targetDir}_{seq}";
                }

                Directory.CreateDirectory(targetDir);

                // 收集所有需要移动的文件（tdms + 关联的 index/sha256 文件）
                var filesToMove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tdmsPath in writtenFiles)
                {
                    if (File.Exists(tdmsPath))
                        filesToMove.Add(tdmsPath);
                    
                    // .tdms_index 文件
                    var indexPath = tdmsPath + "_index";
                    if (File.Exists(indexPath))
                        filesToMove.Add(indexPath);
                    
                    // .sha256 清单文件
                    var sha256Path = Path.ChangeExtension(tdmsPath, ".sha256");
                    if (File.Exists(sha256Path))
                        filesToMove.Add(sha256Path);
                }

                // 移动守卫日志文件
                var guardPath = Path.Combine(basePath, GuardFileName);
                if (File.Exists(guardPath))
                    filesToMove.Add(guardPath);

                // 执行文件移动
                foreach (var srcPath in filesToMove)
                {
                    try
                    {
                        var fileName = Path.GetFileName(srcPath);
                        var destPath = Path.Combine(targetDir, fileName);
                        File.Move(srcPath, destPath);
                        Console.WriteLine($"[StorageGuard] 已移动: {fileName} -> {folderName}/");

                        // 如果是 tdms 文件，记录新路径
                        if (destPath.EndsWith(".tdms", StringComparison.OrdinalIgnoreCase) 
                            && !destPath.EndsWith(".tdms_index", StringComparison.OrdinalIgnoreCase))
                        {
                            newPaths.Add(destPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[StorageGuard] 移动文件失败 {Path.GetFileName(srcPath)}: {ex.Message}");
                        // 移动失败时保留原路径
                        if (srcPath.EndsWith(".tdms", StringComparison.OrdinalIgnoreCase)
                            && !srcPath.EndsWith(".tdms_index", StringComparison.OrdinalIgnoreCase))
                        {
                            newPaths.Add(srcPath);
                        }
                    }
                }

                Console.WriteLine($"[StorageGuard] 文件已整理到: {targetDir}");
                
                // 整理完成后清理状态
                _basePath = null;
                _sessionName = null;
                
                return (targetDir, newPaths);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 整理文件到时间文件夹失败: {ex.Message}");
                // 失败时返回原始路径
                newPaths.AddRange(writtenFiles);
                return (null, newPaths);
            }
        }

        /// <summary>
        /// 清理文件夹名称中的非法字符
        /// </summary>
        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 检查指定目录是否存在上次异常退出的写入会话。
        /// 如果存在，返回恢复信息；否则返回 null。
        /// </summary>
        public static GuardRecoveryInfo? CheckRecovery(string basePath)
        {
            try
            {
                var guardPath = Path.Combine(basePath, GuardFileName);
                if (!File.Exists(guardPath)) return null;

                var json = File.ReadAllText(guardPath);
                var log = JsonSerializer.Deserialize<GuardLog>(json);
                if (log == null) return null;

                // 只有状态为 "Writing" 才意味着异常退出
                if (log.State != nameof(GuardState.Writing)) return null;

                return new GuardRecoveryInfo
                {
                    SessionName = log.SessionName ?? "",
                    StartTimeUtc = log.StartTimeUtc,
                    LastFlushTimeUtc = log.LastFlushTimeUtc,
                    FlushCount = log.FlushCount,
                    GuardFilePath = guardPath
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 检查恢复信息失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 清除恢复标记（用户确认后调用）
        /// </summary>
        public static void ClearRecovery(string basePath)
        {
            try
            {
                var guardPath = Path.Combine(basePath, GuardFileName);
                if (File.Exists(guardPath))
                    File.Delete(guardPath);
            }
            catch { /* best effort */ }
        }

        // ======================== 内部实现 ========================

        /// <summary>
        /// 周期性刷盘定时器回调
        /// </summary>
        private static void OnFlushTimerTick(object? state)
        {
            PerformEmergencyFlush("定时刷盘");
        }

        /// <summary>
        /// 进程退出事件
        /// </summary>
        private static void OnProcessExit(object? sender, EventArgs e)
        {
            PerformEmergencyFlush("进程退出(ProcessExit)");
            EmergencySave();
        }

        /// <summary>
        /// Ctrl+C / 控制台关闭事件
        /// </summary>
        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            PerformEmergencyFlush("控制台中断(CancelKeyPress)");
            EmergencySave();
        }

        /// <summary>
        /// 未处理异常事件
        /// </summary>
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            PerformEmergencyFlush("未处理异常(UnhandledException)");
            EmergencySave();
        }

        /// <summary>
        /// 核心：执行紧急缓冲刷写。将内存中的数据批次写入 TDMS 文件。
        /// 此方法必须尽量快速、不抛异常。
        /// </summary>
        private static void PerformEmergencyFlush(string reason)
        {
            // 使用 Monitor.TryEnter 避免在进程退出时死锁
            bool lockTaken = false;
            try
            {
                lockTaken = Monitor.TryEnter(_lock, TimeSpan.FromSeconds(2));
                if (!lockTaken)
                {
                    Console.WriteLine($"[StorageGuard] {reason}: 无法获取锁，跳过刷盘");
                    return;
                }

                if (!_active || _storage == null) return;

                _storage.Flush();
                _flushCount++;
                _lastFlushUtc = DateTime.UtcNow;

                // 更新守卫日志中的最后刷盘时间
                WriteGuardLog("", GuardState.Writing, updateOnly: true);

                Console.WriteLine($"[StorageGuard] {reason}: 刷盘成功 (第{_flushCount}次, {_lastFlushUtc:HH:mm:ss})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] {reason}: 刷盘异常 - {ex.Message}");
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_lock);
            }
        }

        /// <summary>
        /// 紧急保存：在进程退出场景下，直接调用底层 TDMS DDC_SaveFile 
        /// 将文件元数据和索引写入磁盘，确保 .tdms 和 .tdms_index 文件完整可读。
        /// </summary>
        private static void EmergencySave()
        {
            try
            {
                if (_storage is TdmsSingleFileStorage single)
                {
                    EmergencySaveSingleFile(single);
                }
                else if (_storage is TdmsPerChannelStorage perChannel)
                {
                    EmergencySavePerChannel(perChannel);
                }

                // 保存 SHA-256 指纹清单（尽力而为）
                SaveHashManifest();

                // 紧急整理：将文件移动到时间命名文件夹
                try
                {
                    var files = _storage?.GetWrittenFiles();
                    if (files != null && files.Count > 0)
                    {
                        OrganizeToTimestampFolder(files);
                    }
                }
                catch (Exception orgEx)
                {
                    Console.WriteLine($"[StorageGuard] 紧急整理文件夹失败: {orgEx.Message}");
                }

                Console.WriteLine("[StorageGuard] 紧急保存完成");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 紧急保存异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 单文件模式的紧急保存：通过反射获取文件句柄并调用 DDC_SaveFile
        /// </summary>
        private static void EmergencySaveSingleFile(TdmsSingleFileStorage storage)
        {
            try
            {
                // 通过反射获取 _file 字段（private IntPtr）
                var fileField = typeof(TdmsSingleFileStorage)
                    .GetField("_file", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fileField == null) return;

                var filePtr = (IntPtr)(fileField.GetValue(storage) ?? IntPtr.Zero);
                if (filePtr != IntPtr.Zero)
                {
                    TdmsNative.DDC_SaveFile(filePtr);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 单文件紧急保存异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 多文件模式的紧急保存：通过反射获取每个通道的文件句柄并逐个保存
        /// </summary>
        private static void EmergencySavePerChannel(TdmsPerChannelStorage storage)
        {
            try
            {
                var filesField = typeof(TdmsPerChannelStorage)
                    .GetField("_files", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (filesField == null) return;

                var files = filesField.GetValue(storage) as System.Collections.Concurrent.ConcurrentDictionary<int, IntPtr>;
                if (files == null) return;

                foreach (var kvp in files)
                {
                    if (kvp.Value != IntPtr.Zero)
                    {
                        try { TdmsNative.DDC_SaveFile(kvp.Value); }
                        catch { /* best effort per channel */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 多文件紧急保存异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 尽力保存 SHA-256 指纹清单，使得即使异常退出后也能验证已保存的数据完整性
        /// </summary>
        private static void SaveHashManifest()
        {
            try
            {
                if (_storage == null) return;

                var hashes = _storage.GetWriteHashes();
                var counts = _storage.GetWriteSampleCounts();
                var files = _storage.GetWrittenFiles();

                if (files == null || files.Count == 0) return;

                foreach (var fp in files)
                {
                    try
                    {
                        StorageVerifier.SaveManifest(fp, hashes, counts);
                    }
                    catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
        }

        // ======================== 守卫日志 ========================

        private enum GuardState
        {
            Writing,
            Completed
        }

        private static void WriteGuardLog(string sessionName, GuardState state, bool updateOnly = false)
        {
            try
            {
                if (string.IsNullOrEmpty(_guardFilePath)) return;

                GuardLog log;

                if (updateOnly && File.Exists(_guardFilePath))
                {
                    // 仅更新刷盘时间和计数，保留原始信息
                    try
                    {
                        var existing = JsonSerializer.Deserialize<GuardLog>(File.ReadAllText(_guardFilePath));
                        if (existing != null)
                        {
                            existing.LastFlushTimeUtc = _lastFlushUtc;
                            existing.FlushCount = _flushCount;
                            existing.State = nameof(state);
                            log = existing;
                        }
                        else
                        {
                            log = CreateNewLog(sessionName, state);
                        }
                    }
                    catch
                    {
                        log = CreateNewLog(sessionName, state);
                    }
                }
                else
                {
                    log = CreateNewLog(sessionName, state);
                }

                var json = JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true });

                // 使用 FileStream + Flush(flushToDisk: true) 确保写入物理磁盘
                using var fs = new FileStream(_guardFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sw = new StreamWriter(fs);
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StorageGuard] 写入守卫日志失败: {ex.Message}");
            }
        }

        private static GuardLog CreateNewLog(string sessionName, GuardState state)
        {
            return new GuardLog
            {
                SessionName = sessionName,
                State = nameof(state),
                StartTimeUtc = DateTime.UtcNow,
                LastFlushTimeUtc = _lastFlushUtc,
                FlushCount = _flushCount,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId
            };
        }

        // ======================== 数据模型 ========================

        private class GuardLog
        {
            public string? SessionName { get; set; }
            public string? State { get; set; }
            public DateTime StartTimeUtc { get; set; }
            public DateTime LastFlushTimeUtc { get; set; }
            public long FlushCount { get; set; }
            public string? MachineName { get; set; }
            public int ProcessId { get; set; }
        }

        /// <summary>
        /// 异常退出恢复信息
        /// </summary>
        public class GuardRecoveryInfo
        {
            /// <summary>上次写入的会话名</summary>
            public string SessionName { get; init; } = "";
            /// <summary>写入开始时间 (UTC)</summary>
            public DateTime StartTimeUtc { get; init; }
            /// <summary>最后一次成功刷盘时间 (UTC)</summary>
            public DateTime LastFlushTimeUtc { get; init; }
            /// <summary>总刷盘次数</summary>
            public long FlushCount { get; init; }
            /// <summary>守卫文件路径</summary>
            public string GuardFilePath { get; init; } = "";

            /// <summary>
            /// 格式化为用户可读的恢复提示
            /// </summary>
            public string ToUserMessage()
            {
                var localStart = StartTimeUtc.ToLocalTime();
                var localFlush = LastFlushTimeUtc.ToLocalTime();
                return $"检测到上次写入会话未正常结束（可能是断电或程序异常退出）。\n" +
                       $"会话名: {SessionName}\n" +
                       $"开始时间: {localStart:yyyy-MM-dd HH:mm:ss}\n" +
                       $"最后刷盘: {localFlush:yyyy-MM-dd HH:mm:ss}\n" +
                       $"已刷盘次数: {FlushCount}\n\n" +
                       $"已写入的数据文件应可正常读取（数据保留到最后一次刷盘时刻）。";
            }
        }
    }
}
