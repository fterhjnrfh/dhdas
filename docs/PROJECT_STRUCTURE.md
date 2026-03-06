# 项目结构与目录说明

本文档对 `d:\DH2` 项目的目录结构、用途、关键配置与资源位置进行说明，便于开发与维护。

## 1. 根目录结构概述

- `\DH.sln`：解决方案入口，包含所有子项目。
- `\DH.AppHost\`：根级启动器项目，引用 `DH.Client.App`，用于 `dotnet run` 或 VS 默认启动客户端。
- `\NuGet.Config`：NuGet 源与包管理配置。
- `\.vs\`：Visual Studio 工作区缓存与索引（无需提交）。
- `\build\`：构建与脚手架脚本。
  - `\build\scripts\new-dh.ps1`：新模块脚手架脚本。
- `\build_client.txt` / `\build_output.txt`：构建输出日志（本地生成）。
- `\data\`：示例与演示数据文件（TDMS）。
  - 示例：`session.tdms`、`session2.tdms` 及对应 `*_index`。
- `\samples\`：独立示例与实验性验证代码（不影响主程序）。
  - `\samples\LZ4Compression\`：LZ4 压缩/解压验证控制台程序，与 TDMS 写入逻辑保持一致，用于复现压缩问题。
- `\src\`：项目源码（各子项目）。
- `\tdms\`：TDMS 独立示例与官方依赖。
- `\tests\`：测试目录（单元/集成测试占位）。
- `\tools\TdmsSmokeTest\`：TDMS 资源冒烟测试工具。

## 2. 主要文件夹用途说明

- `\src\DH.Client.App\`：桌面客户端（Avalonia UI）。
  - `App.axaml` / `App.axaml.cs`：应用资源与启动配置。
  - `Program.cs`：程序入口。
  - `app.manifest`：Windows 清单（权限、DPI 等）。
  - `Views\`：XAML 视图（例如 `TdmsViewerView.axaml`、`CurvePanel.axaml`、`ChannelSelector.axaml`）。
  - `ViewModels\`：视图模型（例如 `MainWindowViewModel.cs`、`TdmsViewerViewModel.cs`、`CurvePanelViewModel.cs`）。
  - `Services\`：服务与存储封装（例如 `Storage\TdmsReaderUtil.cs`、`Storage\TdmsNative.cs`）。
  - `Data\`：客户端侧数据与支持文件（运行时会复制 USI DataModels 等）。
  - `Converters\` / `controls\`：UI 转换器与自定义控件。
  - `bin\` / `obj\`：编译产物与中间文件。

- `\src\DH.Driver\`：驱动层（TCP 驱动管理与数据接入）。
  - `TcpDriverManager.cs`：驱动管理器（连接状态、事件分发）。
  - `TcpWorker.cs`：TCP 工作线程。
  - `DataProcessors\`：数据处理管线。
  - `Mock\`：模拟驱动与测试用例。
  - `Tcpdriver\`：TCP 驱动实现细节。

- `\src\DH.UI\` (`NewAvalonia`)：流程编排/GUI 设计器（Avalonia）。
  - `Views\DesignerWorkbenchView.axaml`：核心 GUI 设计工作台（可嵌入其它应用）。
  - `MainWindow.axaml`：独立运行时的窗口外壳，内部托管上述工作台。
  - `AlgorithmModule\`、`FlowEditor\` 等：算法模块、流程节点与运行逻辑。

- `\src\DH.Display\`：显示层（绘图与实时显示组件）。
  - `Realtime\`：实时绘图相关类型与工具。

- `\src\DH.Datamanage\`：数据管理层。
  - `Bus\`：总线与消息分发。
  - `Realtime\`：实时数据管理与订阅。

- `\src\DH.Contracts\`：跨模块共享契约与模型。
  - `Abstractions\`：抽象与接口。
  - `ITcpDriver.cs`：TCP 驱动接口定义。
  - `Models\`：通用数据模型（如曲线点、事件参数等）。

- `\src\DH.Configmanage\`：配置管理。
  - `Providers\`：配置提供者实现。
  - `MockConfig\`：模拟/默认配置资源。

- `\src\DH.Algorithms\`：算法库。
  - `Builtins\`：内置算法集合。

- `\tdms\`：TDMS 独立项目与官方依赖目录。
  - `tdms.csproj` / `tdms.sln`：示例/工具项目。
  - `Program.cs`：示例入口。
  - `TDM C DLL[官方源文件]\dev\bin\64-bit\`：官方 64 位 TDMS/USI 依赖 DLL（如 `nilibddc.dll`、`usiPluginTDM.dll` 等）。
  - `doc\` / `samples\`：官方文档与示例（只读）。

- `\tools\TdmsSmokeTest\`：冒烟测试工具（快速验证 TDMS/USI 资源可用性）。
  - `Program.cs`：测试主入口。
  - `TdmsSmokeTest.csproj`：工具项目文件。

## 3. 关键配置文件描述

- 根级 `\DH.sln`：统一管理各子项目的解决方案文件。
- 根级 `\NuGet.Config`：定义包源与镜像，加速依赖安装。
- `\src\DH.Client.App\DH.Client.App.csproj`：客户端项目配置（依赖、目标框架、资源打包）。
- `\src\DH.Client.App\app.manifest`：进程级配置（权限、兼容性、DPI）。
- `\src\DH.Client.App\App.axaml`：应用级样式与资源字典；主题/字体等 UI 配置。
- `\src\DH.Client.App\Services\Storage\TdmsNative.cs`：运行时环境配置入口（设置 `PATH`、`USI_PLUGINSPATH`、`USI_LOGFILE`、`USI_LOG_LEVEL`、`USICORERESOURCEDIR` 等）。
- 每个子项目的 `*.csproj`：本项目的编译/引用配置（例如 `\src\DH.Driver\DH.Driver.csproj`、`\src\DH.Display\DH.Display.csproj` 等）。

## 4. 资源文件存放位置说明

- 运行数据示例：`\data\`（示例 TDMS 文件，供客户端叠加绘制与读取测试）。
- USI 数据模型：开发时位于 `\src\DH.Client.App\DataModels\`；运行时复制到 `\bin\Debug\net8.0\DataModels\` 与 `\Shared\UsiCore\DataModels\USI\`（由 `TdmsNative.TryPreload()` 负责准备与复制）。
- TDMS/USI 依赖 DLL：`\tdms\TDM C DLL[官方源文件]\dev\bin\64-bit\`（客户端运行时通过 `AddDllDirectory` 以及 `PATH` 环境变量引用）。
- UI 资源：`\src\DH.Client.App\Views\` 与 `\src\DH.Client.App\controls\`（XAML 与自定义控件）。
- 插件加载目录（运行时期望）：`\DataModels\`、`\Shared\UsiCore\Plugins\`（若存在会加入 `USI_PLUGINSPATH`）。

## 5. 测试相关目录用途

- `\tests\`：单元与集成测试的统一入口目录（当前为占位，建议后续按项目分层新增 `DH.Client.App.Tests` 等）。
- `\tools\TdmsSmokeTest\`：冒烟测试工具，用于快速验证 TDMS/USI 资源准备是否正确（构建后在 `bin\` 下运行）。
- `\src\DH.Client.App\bin\`：客户端编译产物，包含运行期资源与日志（非源码目录）。

## 附录：重要文件与路径索引

- 客户端入口：`\src\DH.Client.App\Program.cs`
- 应用资源：`\src\DH.Client.App\App.axaml`、`\src\DH.Client.App\app.manifest`
- 视图/视图模型：
  - `\src\DH.Client.App\Views\TdmsViewerView.axaml`
  - `\src\DH.Client.App\Views\CurvePanel.axaml`
  - `\src\DH.Client.App\ViewModels\TdmsViewerViewModel.cs`
  - `\src\DH.Client.App\ViewModels\MainWindowViewModel.cs`
- TDMS 读取与环境：
  - `\src\DH.Client.App\Services\Storage\TdmsReaderUtil.cs`
  - `\src\DH.Client.App\Services\Storage\TdmsNative.cs`
  - 依赖 DLL 路径：`\tdms\TDM C DLL[官方源文件]\dev\bin\64-bit\`
- 驱动层：`\src\DH.Driver\TcpDriverManager.cs`、`\src\DH.Driver\TcpWorker.cs`
- 示例数据：`\data\session.tdms`、`\data\session2.tdms`

> 说明：本结构说明基于当前仓库内容，若新增模块或资源，请在相应章节补充用途与路径，并保持层级清晰。

## 存储与写入
- 路径解析：`StoragePath` 相对仓库根，默认 `./data`。
- 模式：`SingleFile` 与 `PerChannel`，接口 `ITdmsStorage` 控制生命周期（Start/Write/Flush/Stop）。
- TDMS-only：
  - 单文件：生成 `sessionName.tdms`；组名 `Session`，组描述为会话名（ASCII 安全）；通道名 `CH{id}`，单位 `V`。
  - 多文件：生成 `sessionName_ch{id}.tdms`；组名 `Session`，组描述为会话名；每文件一个通道 `CH{id}`。
  - 波形属性：单文件与多文件均写入 `wf_xname="Time"`、`wf_xunit_string="s"`、`wf_increment=0.001`（秒）。
  - 批量写入：按通道缓冲，默认批量 `4096` 样本后调用 `DDC_SetDataValuesDouble`，减少原生调用次数与磁盘写入频率。
  - 刷新/保存：`Flush` 仅刷新缓冲（写入通道数据，不执行 `DDC_SaveFile`）；`Stop` 先刷新缓冲，再执行 `DDC_SaveFile` 与 `DDC_CloseFile`。
  - 依赖：需要 `nilibddc.dll` 可用；若未检测到，UI 提示“存储不可用（TDMS-only）”，不再回退至二进制。
- 读取：`TdmsReaderUtil` 支持枚举组/通道与读取 `double[]`；绘图端使用 `wf_increment` 推导时间轴（秒）。

## 变更与性能优化
- 删除 BIN 模式：
  - 移除 `BinarySingleFileStorage.cs` 与 `BinaryPerChannelStorage.cs`。
  - `TdmsSingleFileStorage` 与 `TdmsPerChannelStorage` 不再包含 BIN 回退逻辑。
  - `MainWindowViewModel` 提示文案更新，TDMS 不可用时明确提示不可写入。
- 性能优化（TDMS）：
  - 按通道缓冲批量写入，默认批量 `4096` 样本，显著降低原生 `DDC_SetDataValuesDouble` 的调用次数。
  - 减少频繁 `DDC_SaveFile` 调用，集中在 `Stop` 阶段执行，降低磁盘刷写开销。
  - 在路径/字符集异常时自动切换公共文档 ASCII 路径，提升成功率。
  - 波形属性统一为秒级时间轴，读取端无需推断采样间隔。
- 使用建议：
  - 连续写入时使用较大的批量；如需更低延迟可适当调小批量但会增加调用频率。
  - 保持 `nilibddc.dll`、`usiPluginTDM.dll` 与 `USI` 资源路径有效，避免运行期不可用。

## 修改记录（2025-10-28）

以下为本次会话期间完成的改动的完整记录，按逻辑与时间顺序组织，包含修改内容、修改时间与修改原因，并给出测试与验证信息，便于后续查阅与追溯。

### 1) HoverZoomWeb 交互缩放示例（静态站点）

- 摘要：新增一个用于交互原型验证的静态站点 `HoverZoomWeb`，实现“以鼠标悬停点为中心的滚轮缩放”和平滑渲染队列，用于在移植到客户端前验证交互细节。
- 修改时间：2025-10-28（本会话）
- 变更文件/路径：
  - `tools/HoverZoomWeb/HoverZoomWeb.csproj`
  - `tools/HoverZoomWeb/Program.cs`
  - `tools/HoverZoomWeb/wwwroot/index.html`
  - `tools/HoverZoomWeb/wwwroot/style.css`
  - `tools/HoverZoomWeb/wwwroot/main.js`
- 修改原因：
  - 在桌面客户端改动前，先以轻量原型验证缩放交互、事件委托和性能表现。
  - 形成可视化参考，降低 UI 大改的风险。
- 具体内容：
  - 以 Kestrel 自托管的最小 Web 应用静态托管 `wwwroot`。
  - `index.html` 定义卡片和图像容器，`style.css` 提供基础布局与过渡，`main.js` 实现：
    - 鼠标悬停中心缩放（滚轮）；
    - `requestAnimationFrame` 批处理渲染；
    - 事件委托与边界约束（避免溢出）。
- 测试与验证：
  - 运行站点，预览地址：`http://localhost:5178/`。
  - 验证滚轮缩放、居中对齐、性能帧率。
- 影响范围：
  - 仅限 `tools` 目录下原型代码，对客户端无直接影响。

### 2) HoverZoomWeb 坐标系叠加（原型完善）

- 摘要：在原型上为每个图像容器新增坐标轴叠加层（刻度与标签），用于验证坐标系与缩放的协同表现。
- 修改时间：2025-10-28（本会话）
- 变更文件/路径：
  - `tools/HoverZoomWeb/wwwroot/index.html`（新增 `canvas.axes` 叠加层）
  - `tools/HoverZoomWeb/wwwroot/style.css`（叠加层样式与层级）
  - `tools/HoverZoomWeb/wwwroot/main.js`（坐标轴绘制、`ResizeObserver` 协同）
- 修改原因：
  - 为后续桌面端的坐标系叠加提供交互与视觉参考。
- 具体内容：
  - 通过 `canvas` 绘制 X/Y 轴刻度与标签；监听容器尺寸变更，保持叠加层与内容同步。
- 测试与验证：
  - 在预览页下缩放与移动，验证刻度密度、标签可读性与对齐。

### 3) 客户端坐标刻度与标签（SkiaMultiChannelView）

- 摘要：在桌面客户端的多/单通道渲染中叠加坐标刻度与标签，提升曲线的可读性与定位能力。
- 修改时间：2025-10-28（本会话）
- 变更文件/路径：
  - `src/DH.Client.App/controls/SkiaMultiChannelView.cs`
- 修改原因：
  - 满足结果展示界面对坐标系（刻度/标签）清晰呈现的需求。
- 具体内容：
  - 新增 `DrawAxisTicksAndLabels(...)` 方法，按当前缩放与视图范围绘制网格线、刻度与文字标签；
  - 在 `RenderMultiChannelData(...)` 与 `RenderSingleChannelData(...)` 中调用上述方法，确保在曲线绘制后叠加坐标。
- 测试与验证：
  - 构建并运行客户端，观察多通道/单通道视图的坐标叠加呈现，验证在缩放时标签与刻度更新正确。

### 4) 结果显示界面控件重布局（MainWindow/CurvePanel）

- 摘要：将“可视化控制”统一迁移至主窗口顶部工具栏，并置于“添加视图”按钮左侧，在满足功能完整性的同时避免遮挡曲线，并符合安全与宽度约束。
- 修改时间：2025-10-28（本会话）
- 变更文件/路径：
  - `src/DH.Client.App/Views/MainWindow.axaml`
  - `src/DH.Client.App/Views/MainWindow.axaml.cs`
  - `src/DH.Client.App/Views/CurvePanel.axaml`
  - `src/DH.Client.App/Views/CurvePanel.axaml.cs`
- 修改原因：
  - 根据需求：所有可视化控件移至“添加视图”按钮左侧；保持水平对齐、不遮挡曲线；最小与曲线区域 20px 安全距；控件组总宽不超过画布 30%；缩放时布局保持稳定；控件全部可见且可操作。
- 具体内容：
  - `MainWindow.axaml`：将顶部栏改为三列 `Grid`，左侧为全局可视化控件（X/Y 缩放、重置、通道选择），右侧为 `AddViewButton`/`RemoveViewButton`；通过 `Margin` 与容器约束确保与曲线区域至少 20px 的安全边距；
  - `MainWindow.axaml`：为“通道选择”添加 `Flyout`，在不占用曲线区的前提下提供面板通道选择；
  - `CurvePanel.axaml`：隐藏每面板覆盖层控件（`IsVisible=false`），避免叠加遮挡；
  - `MainWindow.axaml.cs`：
    - 跟踪“活动面板”（鼠标进入或点击的最后一个 `CurvePanel`），将全局控件的事件转发给活动面板；
    - 运行时根据画布宽度限制左侧控件组的最大宽度 ≤ 30%（监听布局变更动态调整）；
    - 将全局通道选择与活动面板同步；修正 Avalonia 事件名为 `PointerEntered`；
  - `CurvePanel.axaml.cs`：新增公共方法以供主窗口调用：`ZoomInX/ZoomOutX/ZoomInY/ZoomOutY/ResetZoom/SetSelectedChannels`，保持原有缩放与自适应逻辑；
  - 统一控件水平对齐至“添加视图”按钮所在一行，控件整齐排列并留有合理间距。
- 约束与设计满足：
  - 安全边距：顶部控件与曲线区域保持 ≥ `20px` 间距；
  - 宽度限制：控件组最大宽度 ≤ 画布宽度 `30%`，并随窗口缩放动态更新；
  - 可视性与可操作性：所有控件始终可见，操作事件由主窗口转发至活动面板，确保功能完整；
  - 缩放稳定性：布局独立于绘制画布，缩放与窗口尺寸变化不改变工具栏的相对布局与约束；
  - 水平对齐：与“添加视图”按钮同行对齐，整洁排列。
- 测试与验证：
  - 构建与运行客户端：构建成功（警告 27 条，主要为可空性提示），功能验证通过；
  - 多视图验证：添加多个 `CurvePanel`，切换活动面板后验证 X/Y 缩放、重置与通道选择均正确转发；
  - 布局验证：缩放窗口观察控件组宽度限制与 20px 安全边距均保持；
  - 事件修正：修复活动面板跟踪事件为 `PointerEntered` 后，活动面板切换正常。
- 影响范围：
  - 主窗口顶部工具栏、每面板的覆盖层控件、缩放/通道选择交互逻辑。

---

### 验证与回溯信息

- 构建状态：主项目构建成功（警告 27 条，均与可空性相关；不影响运行）。
- 运行观察：TDMS 相关 DLL 预加载成功；网格布局更新；Skia 使用 OpenGL 后端渲染；MockDriver 自动启动。界面控件布局与交互符合需求。
- 预览与原型：`HoverZoomWeb` 可在 `http://localhost:5178/` 访问，用于参考交互与坐标叠加表现。

### 后续建议

- 可选增强：
  - 支持“多选视图同时控制”或添加“当前视图指示器”。
  - 为坐标轴叠加添加主题与密度自适应（避免密集标签重叠）。
- 文档维护：后续 UI/交互改动，请在本章节按上述格式新增条目，包含路径、原因、时间与验证步骤。