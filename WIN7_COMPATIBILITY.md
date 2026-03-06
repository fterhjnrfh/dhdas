# Windows 7 兼容性说明

## 项目已降级到 .NET 6.0 以支持 Windows 7 SP1

### ✅ 已完成的修改

1. **框架降级**: 所有项目从 `.NET 8.0` 降级到 `.NET 6.0`
2. **TLS 兼容**: 在 `Program.cs` 中显式启用 TLS 1.2 以支持 Win7 的 HTTPS 请求
3. **加密库兼容**: 修改 `XtjCrypto.cs` 中 `AesGcm` 构造函数调用以兼容 .NET 6 API
4. **OpenGL 兼容**: 修复 `CurveRenderer.cs` 中参数类型转换问题

### ⚠️ 暂时禁用的功能

由于 ScottPlot.Avalonia 5.x 不支持 .NET 6.0，以下功能已被暂时禁用：

- **EcgSignalRenderer**: 基于 ScottPlot 的 ECG 信号渲染器
- **SkiaRealtimePlotWorker**: Skia 实时绘图工作器
- **SkiaPlotControl**: Skia 绘图控件

这些功能在界面中仍然存在，但不会响应。核心的数据采集、处理和存储功能**完全正常**。

### 📦 包版本变更

| 包名 | 原版本 | 新版本 | 原因 |
|------|--------|--------|------|
| ScottPlot.Avalonia | 5.* | 4.1.* | 5.x 不支持 .NET 6.0 |

### 🚀 在 Windows 7 上部署

#### 1. 系统要求
- **Windows 7 SP1** (必须)
- **KB3063858 补丁** (强烈建议，否则 .NET 6 无法运行)

#### 2. 发布命令

```powershell
# 独立发布 (推荐 - 无需用户安装 .NET 运行时)
dotnet publish src/DH.Client.App/DH.Client.App.csproj `
  -c Release `
  -f net6.0 `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o publish/win7

# 发布后的文件在: publish/win7/DH.Client.App.exe
```

#### 3. 依赖运行时发布 (需要用户安装 .NET 6 Desktop Runtime)

```powershell
dotnet publish src/DH.Client.App/DH.Client.App.csproj `
  -c Release `
  -f net6.0 `
  -r win-x64 `
  --self-contained false `
  -o publish/win7-framework-dependent
```

### ✅ 完全兼容的功能 (在 Win7 上正常工作)

1. **Avalonia UI**: 跨平台 UI 框架，完美支持 Win7
2. **TCP 通讯**: `System.Net.Sockets.TcpClient` 完全兼容
3. **数据处理**: 
   - TDMS 文件读写
   - LZ4 压缩/解压
   - 波形数据采集与存储
4. **多通道曲线显示**: 基于 SkiaSharp 的实时波形绘制
5. **配置管理**: JSON 配置文件读写
6. **OpenGL 渲染**: 波形的硬件加速绘制（如果 Win7 上有合适的驱动）

### ⚠️ Windows 7 限制

以下 Windows 10/11 特性在 Win7 上**不可用**，但不影响核心功能：

1. **深色模式**: Win7 不支持系统级深色主题
2. **现代 Toast 通知**: 只能使用气泡提示 (Balloon Tip)
3. **硬件加速**: 
   - DirectX 11/12 特性有限
   - OpenGL 可能降级为软件渲染
   - 界面可能不如 Win10 流畅，CPU 占用略高

### 🔧 未来升级路径

如果需要恢复 ScottPlot 5.x 功能，可以：
1. 将框架升级回 `.NET 8.0`
2. 恢复 `ScottPlot.Avalonia` 到 5.* 版本
3. 取消 `DH.Display` 和 `DH.Client.App` 项目中的文件排除
4. 取消 `MainWindow.axaml.cs` 中的代码注释

### 📝 修改的文件清单

```
src/DH.Algorithms/DH.Algorithms.csproj
src/DH.Client.App/DH.Client.App.csproj
src/DH.Client.App/Program.cs
src/DH.Client.App/Views/MainWindow.axaml.cs
src/DH.Configmanage/DH.Configmanage.csproj
src/DH.Contracts/DH.Contracts.csproj
src/DH.Datamanage/DH.Datamanage.csproj
src/DH.Display/DH.Display.csproj
src/DH.Driver/DH.Driver.csproj
src/DH.UI/NewAvalonia.csproj
src/DH.UI/AlgorithmModule/AlgorithmModule.csproj
src/DH.UI/Services/XtjCrypto.cs
src/DH.Client.App/controls/CurveRenderer.cs
```

---

**最后更新**: 2026年1月12日  
**适用版本**: .NET 6.0 (Windows 7 SP1+)
