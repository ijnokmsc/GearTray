# 2.4G 设备电量与设置中心 — 架构设计

> 目标平台：Windows 10 1809+ / Windows 11
> 技术栈：C# / .NET 8（LTS）+ WPF
> 扩展模型：声明式描述符（JSON）+ 可选 DLL 驱动，双轨并存
> 分发模型：纯离线，插件从本地 `plugins/` 目录加载，不联网、不自动更新

---

## 1. 设计目标与硬约束

### 1.1 目标

1. 托盘常驻，实时显示每台 2.4G 设备的电量；多设备多图标，图标本身就是电量数字。
2. 一套程序兼容多种设备，新增设备**优先不改主程序**。
3. 不只是"看电量"：能写设置（鼠标 DPI、Windows 键锁定、回报率）。
4. 本机首批设备：`MCHOSE A7 V2 Ultra+`（鼠标）、`VGN`（键盘）。
5. 插件不只适配设备，还能**往面板里嵌自己的界面**（槽位制 + 推子页）；灯效、宏编辑这类复杂功能不必等主程序更新。
6. **外观可换**：多套皮肤（Token 包热切换）+ 四套内置布局模板，皮肤可自行扩写，不重编译主程序。

### 1.2 硬约束

| 约束 | 说明 |
|---|---|
| 不联网 | 无 HTTP / 遥测 / 自动更新。所有读取在本机 HID 层完成 |
| 不落日志 | 默认零日志文件；`--debug` 时才输出到调试通道 |
| 空闲近零 CPU | 事件驱动 + 定时器，禁止 `while(true) { sleep }` 轮询 |
| 无僵尸进程 | 退出必须真正结束进程、清理托盘图标、释放单实例锁 |
| 单实例 | 全局 Mutex；重复启动时激活已有实例并开面板 |
| 双句柄铁律 | 同一 HID 句柄**绝不**并发阻塞读 + 写（会静默杀进程） |

---

## 2. 参考项目的局限：为什么要重构

参考实现（`2.4G-battery-viewer`）是一份很好的**协议逆向笔记**，但作为软件是单点方案：

| 维度 | 参考实现 | 本设计 |
|---|---|---|
| 设备接入 | 协议表 + 代码内 `if/else` | 描述符声明 / DLL 驱动，主程序零改动 |
| 抽象层次 | 直接调 HID，业务与协议耦合 | 传输 → 协议 → 语义 → 表现，四层解耦 |
| 能力模型 | 只有"读电量" | 能力集合（电量 / DPI / 键锁 / 回报率），UI 由能力动态生成 |
| 写入能力 | 无 | 完整的 get/set 对称命令 |
| 复用价值 | 换设备要改源码 | 内置"协议探测/扫描器"，把逆向过程工具化 |

**核心判断**：参考项目不可扩展的根因不是语言，而是**协议写死在调用点**。所以第一优先级是把"协议"变成**数据**。

---

## 3. 整体设计思路：四条原则

### 原则一：协议即数据（Protocol as Data）

能声明的东西绝不写代码。设备的"怎么问、怎么解码"是一段 JSON，不是一段 C#。

### 原则二：能力即契约，UI 即投影

设备对外暴露的不是"我是鼠标"，而是"我支持 `IBatterySource` + `IDpiControl` + `IWinKeyLockControl`"。
界面不写死任何设备名，**按能力集合渲染控件**。新设备只要声称支持 DPI，DPI 控件自动出现。

### 原则三：传输与业务彻底分离

`ITransport` 只懂"发一帧、收一帧、超时"。它不知道什么是电量。
这样未来加蓝牙 HID、串口 dongle、甚至虚拟设备（做测试）都不用动上层。

### 原则四：把逆向过程工具化

参考项目最值钱的是"怎么摸协议"。这套工具内置**探测工作台**：枚举接口 → 转储 Feature Report → 试探 Report ID → 字节扫描找电量 → 生成描述符草稿。
新设备接入从"写代码"变成"跑一遍工作台，导出一份 JSON"。

---

## 4. 架构分层

```
┌──────────────────────────────────────────────────────────────┐
│ 表示层  Host.Wpf                                             │
│  TrayIconHost │ FlyoutPanel │ SettingsWindow │ ProbeWorkbench │
├──────────────────────────────────────────────────────────────┤
│ 应用服务  Core.Application                                    │
│  DeviceRegistry │ PollScheduler │ CapabilityBroker │          │
│  SettingsStore  │ SessionSupervisor │ SingleInstanceGuard      │
├──────────────────────────────────────────────────────────────┤
│ 抽象契约  Core.Abstractions                                   │
│  IDeviceDriver │ IDeviceSession │ ITransport │ Capability     │
│  BatteryLevel │ DpiProfile │ DeviceIdentity │ Descriptor      │
├──────────────────────────────────────────────────────────────┤
│ 驱动实现  Drivers.*                                          │
│  GenericHidDriver │ DescriptorDriver │ McHoseDriver │ VgnDriver│
├──────────────────────────────────────────────────────────────┤
│ 传输层  Transports.Hid                                        │
│  HidEnumerator │ ControlChannel │ ListenChannel │ DeviceWatcher│
└──────────────────────────────────────────────────────────────┘
                     ↓ Win32
        SetupAPI / hid.dll / WM_DEVICECHANGE
```

依赖方向严格单向向下。`Host.Wpf` 不引用任何 `Drivers.*`；它只认识 `Core.Abstractions`。
`Drivers.*` 不引用 `Host.*`，可独立单测。

### 4.1 为什么不用 MVVM 框架把 HID 包进 ViewModel

HID 会话是有状态、可超时、可热插拔的资源，塞进 ViewModel 会导致生命周期与 UI 绑定。
本设计里 UI 只持有一个**只读快照**（`DeviceSnapshot`），由 `DeviceRegistry` 在后台线程更新并推送到 UI 线程。
UI 线程永远不阻塞在 HID 上——这是托盘程序不卡的根本。

---

## 5. 核心抽象：能力模型

设备被建模为「身份 + 能力集合」。

```csharp
public sealed record DeviceIdentity(
    ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage,
    string? ProductName, string? SerialNumber, string DevicePath);

public interface IDeviceSession : IAsyncDisposable
{
    DeviceIdentity Identity { get; }
    IReadOnlySet<DeviceCapability> Capabilities { get; }
    DeviceHealth Health { get; }          // Online / Asleep / Stale / Faulted

    Task<BatteryLevel?> ReadBatteryAsync(CancellationToken ct);
}

public enum DeviceCapability
{
    Battery, DpiProfile, WinKeyLock, PollingRate, ProfileSwitch, FirmwareInfo
}
```

**可选能力用独立接口表达，不往 `IDeviceSession` 里堆方法。**
实现类多了接口，UI 就多画一块控件；没实现的设备自然没有那块，不需要 capability 枚举二次判断：

```csharp
// 能改 DPI 的实现这个；探测靠 session is IDpiControl
public interface IDpiControl { ... }

// Win 锁：必须如实暴露是固件态还是软件钩子
public interface IWinKeyLockControl { ... }

// 可调固件项：字段表由驱动给出，UI 不认识任何具体设备
public interface IDeviceSettings
{
    IReadOnlyList<DeviceSetting> Settings { get; }
    bool IsAvailable { get; }
    string? LastError { get; }

    Task RefreshAsync(CancellationToken ct);
    bool SetSetting(string key, int value);   // 返回是否已回读确认
}

public sealed record DeviceSetting(
    string Key, string Group, string Label,
    DeviceSettingKind Kind,          // Toggle / Choice / Color
    int Value,                       // 设备侧原始值，不做语义映射
    IReadOnlyList<DeviceSettingOption> Options,
    string? Hint = null)
{
    public bool IsOn => Options.Count > 0 && Value == Options[0].Value;
}
```

约定：`Toggle` 的 `Options[0]` 恒为「开」。有些固件位是 `0 = 开`（灯效开关、六键无冲），
把它们的第一项写成 0 即可 —— 不另设「反相」标志，那个标志会和选项表互相打脸。

**语义层不做设备判断**。`CapabilityBroker` 拿到能力集合后决定：
托盘图标要不要显示百分比、面板里要显示哪几张卡片、设置页要生成哪些控件。

### 5.1 电量语义要能表达"不确定"

这是参考项目的一个隐性缺陷：读不到就显示"没有可用的读取方式"，用户无法区分"设备没电"和"设备在睡觉"。

```csharp
public readonly record struct BatteryLevel(
    int? Percent,            // null = 未知
    BatteryState State,      // Discharging / Charging / Full / Unknown
    DateTimeOffset SampledAt,
    bool IsStale);           // true = 本次未取到，沿用上次值
```

鼠标休眠时收不到回复属于**正常现象**。正确处理是：保留上次值 + 标记 `IsStale` + tooltip 加"（动一下鼠标刷新）"，
而不是把界面清空成"读取失败"。

---

## 6. 插件机制（双轨 × 三层界面）

> 两个正交维度决定一个插件能干什么：
> **轨道**（Track A/B）决定「协议怎么实现」，**界面层级**（L0/L1/L2）决定「界面怎么写」。
> 不要混为一谈——一个 Track A 描述符插件可以有很丰富的界面（L0/L1），一个 Track B 驱动也可以没有界面。

### 6.1 轨道划分

| | Track A：声明式描述符 | Track B：DLL 驱动 |
|---|---|---|
| 形态 | `plugins/*/device.json` | `plugins/*/Driver.dll` |
| 适合 | 单帧请求、固定偏移解码、CRC16 校验 | 多轮握手、状态机、profile 结构体、动态通道 |
| 改设备成本 | 改 JSON，重启插件宿主即生效 | 改代码，重新编译 |
| 能力上限 | 中（内置求值器能表达的） | 全 |
| 安全边界 | 纯数据，无执行权 | 进程内代码，完全信任 |
| 界面能力 | 仅 L0/L1 声明式（宿主渲染） | L0/L1/L2 全部（可自带控件） |
| 卸载成本 | 重载描述符即生效 | 无 UI：热重载；带 UI（L2）：需重启 |

**判定标准（写进文档，避免反复纠结）**：
一条命令能在「一次请求 + 一次应答 + 纯字节变换」内完成 → Track A。
需要"发 A 收 B 再根据 B 决定发 C"→ Track B。

按此标准：**电量读取绝大多数走 Track A**；**DPI 写入与 Windows 键锁定通常走 Track B**（一般要带握手、序号、CRC、读回校验）。

### 6.2 Track A：描述符格式

```jsonc
{
  "schema": "device-v1",
  "id": "mchose.a7v2ultra-plus",
  "displayName": "MCHOSE A7 V2 Ultra+",
  "vendor": "MCHOSE",

  // 匹配规则：字段越具体打分越高，用于消歧
  "match": {
    "vid": "0x373B",
    "pid": "0x1216",
    "usagePage": "0xFF05",
    "productContains": "A7 V2 Ultra"
  },

  "transport": {
    "kind": "hid",
    "reportId": 8,
    "reportSize": 64,
    "channel": "output",          // output | feature
    "replyChannel": "input"
  },

  "commands": {
    "readBattery": {
      "request": "08 7D 72 02 00 01 07 01",
      "timeoutMs": 300,
      "retry": 2,
      "validate": [
        { "offset": 1, "equals": "0x72" },
        { "offset": 5, "equals": "0x07" }
      ],
      "extract": [
        { "offset": 7, "type": "u8", "scale": 1.0, "clamp": [0, 100], "as": "percent" }
      ],
      "onTimeout": "markStale"      // 另一选项：fault
    }
  },

  "capabilities": ["Battery"],

  "settings": [
    {
      "id": "dpi",
      "capability": "DpiProfile",
      "type": "enum",
      "label": "DPI",
      "options": [400, 800, 1600, 3200, 6400, 12800],
      "read": "getDpi",
      "write": "setDpi"
    }
  ],

  "quirks": {
    "needsWakeHint": true,          // 超时提示"动一下鼠标"
    "commandGapMs": 30,             // 连续命令之间的最小间隔
    "avoidConcurrentRead": true
  }
}
```

**内置解码操作**（求值器支持，不引入完整脚本引擎）：

| 操作 | 说明 |
|---|---|
| `u8 / u16le / u16be / u24 / u32` | 按偏移取值 |
| `bit` | 取某字节的某一位（用于键锁标志位） |
| `scale` / `offset` | 线性变换 |
| `map` | 查表映射（如 0x01→DPI 400） |
| `clamp` | 上下限钳制 |
| `crc16-ccitt-false` | 校验，覆盖区间可指定 |
| `checksum8` / `xor8` | 简易和校验 |

> 刻意**不做**完整的 JS/Lua 脚本引擎。理由是：脚本引擎会带来沙箱、性能、调试三重负担，而描述符的目标设备 80% 都很简单；复杂的直接写 DLL 更清晰。

### 6.3 Track B：DLL 契约

```csharp
public interface IDeviceDriver
{
    DriverManifest Manifest { get; }
    int MatchScore(DeviceIdentity id);          // 0 = 不接管
    ValueTask<IDeviceSession> OpenAsync(DeviceIdentity id, ITransport t, CancellationToken ct);
}

public sealed record DriverManifest(
    string Id, string DisplayName, Version Version,
    string MinHostVersion, string Author, string? Description);
```

`MatchScore` 采用**评分竞标**而非注册顺序，避免"后装的插件抢走别人的设备"：

| 命中字段 | 分值 |
|---|---|
| VID + PID + UsagePage | 100 |
| VID + PID | 80 |
| PID + ProductName 匹配 | 60 |
| 仅 VID | 30 |
| 仅 UsagePage 泛型兜底 | 10 |

最高分独占；同分按 `Manifest.Version` 降序，再按插件 id 字典序（保证确定性）。

### 6.4 加载与隔离

- 每个插件一个 `AssemblyLoadContext`（`isCollectible: true`），**依赖不共享**，避免版本冲突。
- 宿主接口（`Core.Abstractions`）通过 `AssemblyDependencyResolver` 回落到宿主版本，保证 `IDeviceDriver` 类型同一性。
- 支持**卸载重载**：调试 DLL 驱动时改代码 → 点"重载插件" → 无需重启托盘。
- 插件加载失败**不影响主程序启动**：记录到诊断面板，托盘照常工作。
- 插件抛出的异常在会话边界被捕获，该设备标记 `Faulted` 并指数退避重试，不冒泡到 UI。

> 因为分发范围是「自用、离线」，本轮**不做**签名校验与沙箱。插件即本地可信代码。

### 6.5 描述符的加载规则

- 目录结构：`plugins/<plugin-id>/{ device.json | Driver.dll | icon.png | README.md }`
- 描述符通过 `System.Text.Json` 严格反序列化 + `schema` 版本检查；未知字段报错而非忽略（防止拼写错误静默失效）。
- schema 主版本不兼容时：跳过并提示，不崩溃。

---

### 6.6 界面扩展：三层 UI 贡献模型

插件写界面有三条路，按「自由度 ↔ 可卸载性」权衡：

| 层 | 形态 | 谁渲染 | 热重载 | 视觉一致性 |
|---|---|---|---|---|
| **L0 设置项** | `settings[]` 声明（type/label/options/range） | 宿主原生控件 | ✅ | 100% |
| **L1 只读卡片** | 声明式 `ui.blocks[]`（键值行 / 档位条 / 迷你图） | 宿主渲染器 | ✅ | 100% |
| **L2 自定义视图** | 插件自带 `FrameworkElement`（代码构建或 XAML） | 插件 | ❌ 需重启 | 靠 Token + 控件工厂约束 |

**必须按 L0 → L1 → L2 顺序选**。L0/L1 的界面是"数据"，宿主统一渲染，天然跟随主题与 DPI 缩放，还能热重载；只有真正需要"交互画布"（灯效矩阵、宏编辑、曲线调参）才值得上 L2。

这条规则要写进插件模板 README——否则第一个插件就会直接上 L2，然后三个插件的面板变成三种风格。

#### L0 / L1 描述符示例

```jsonc
"settings": [
  { "id": "dpi", "type": "enum", "label": "DPI", "options": [800, 1600, 3200] }
],

"ui": {
  "blocks": [
    { "slot": "DeviceCardExtension", "order": 10,
      "type": "segment", "label": "DPI", "source": "getDpi", "write": "setDpi",
      "options": [800, 1600, 3200] },

    { "slot": "DeviceDetailTab", "title": "状态", "order": 20,
      "type": "keyvalue", "rows": [
        { "label": "回报率", "source": "getPollingRate", "unit": "Hz" },
        { "label": "固件",   "source": "readFirmware" }
      ] }
  ]
}
```

L1 的 block 类型刻意少而精：`keyvalue` / `segment`（档位切换）/ `toggle` / `meter`（进度条）/ `sparkline`（近 24 次采样曲线）。
覆盖了 90% 的"想给设备加个小控件"需求，且全部由宿主绘制。

#### L2 契约

```csharp
// 由插件 DLL 实现，宿主反射发现（标记接口，不强制基类）
public interface IUiPlugin
{
    IEnumerable<UiContribution> GetContributions(IUiHostContext host);
}

public sealed record UiContribution(
    UiSlot Slot,
    string Title,
    Geometry? Icon,              // 矢量几何，不用图片文件
    int Order,
    Func<IPluginView> Factory);  // 懒构造

public interface IPluginView : IDisposable
{
    FrameworkElement Root { get; }
    ValueTask OnActivatedAsync(CancellationToken ct);   // 首次可见时调用
    ValueTask OnDeactivatedAsync();                     // 被页面盖住/面板关闭
    void OnDeviceSnapshot(DeviceSnapshot snapshot);     // 推送式，插件不自建轮询
}
```

四条硬约束：

1. **`Factory` 只在视图第一次可见时调用**。面板没打开 → 插件视图从未构造 → 零内存零 CPU。这是托盘程序"空闲近零占用"的关键，不能图省事在启动时全构造。
2. **`Root` 只能在 UI 线程创建**。宿主在调用 `Factory` 前断言 `Dispatcher.CheckAccess()`，违反直接抛异常（比渲染错乱好排查）。
3. **数据靠推送，不靠插件自己拉**。宿主已有的轮询结果直接喂给 `OnDeviceSnapshot`，插件禁止自建 `DispatcherTimer` 去读设备——否则同一 dongle 上会出现两条并发命令流，私有协议必炸。
4. **`Dispose` 必须解除事件订阅 / 停 timer / 清 binding**。诊断面板会用弱引用检测"Dispose 后仍被订阅持有"的插件并点名——这是 WPF 插件内存泄漏的头号来源。

#### 能力收口：`IUiHostContext`

插件**不能**直接碰 `Application.Current`、不能 `new Window()`、不能自己开 HID 句柄。所有能力由宿主注入：

```csharp
public interface IUiHostContext
{
    DeviceSnapshot? Device { get; }          // 不可变快照，不暴露活会话状态
    IDeviceSession? Session { get; }         // 全异步，宿主内部切线程
    IThemeTokens    Theme { get; }           // 只给语义 token，不给 Brush 对象
    IControlFactory Controls { get; }        // 宿主控件工厂
    IUiNavigator    Nav { get; }             // PushPage / PopPage

    IDisposable SubscribeDeviceChanged(Action<DeviceSnapshot> onChanged);
    void RequestRefresh();
    void ReportError(string message, Exception? ex);
}
```

三个刻意的限制：

| 限制 | 理由 |
|---|---|
| `Theme` 只给语义 token（`Surface` / `TextPrimary` / `Accent` / `Danger` / `Mono`），由宿主解析成 `DynamicResource` key | 切浅色/深色、切高对比度、切 DPI，插件界面自动跟随，零改动 |
| `Controls` 提供 `Card` / `ToggleRow` / `SliderRow` / `Segment` / `BatteryBar` / `IconButton` / `NumberStepper` | 插件"白嫖"视觉一致性；绕过工厂也能画，但会一眼看出不协调 |
| `Session` 全是 async，接口层不提供同步方法 | 插件在 UI 线程 await HID 会直接冻结面板。宿主内部 `Task.Run`，`await` 后自动回 UI 线程——从接口设计上就堵死这条路 |

### 6.7 槽位模型

宿主定义槽位，插件只填内容。下图是 `CardList` 布局模板的示意（另一套 `Showcase` / `Console` / `Sidebar` 见 12.4，槽位落点会变，但槽位集合不变）：

```
┌─ Flyout 360 ──────────────────────────┐
│ ┌ 设备卡片 ─────────────────────────┐ │
│ │ MCHOSE A7 V2 Ultra+        82%    │ │
│ │ ████████████████░░░░              │ │
│ │ [800][1600][3200]                 │ │ ← DeviceCardExtension
│ │ [DPI] [灯效] [关于]               │ │ ← DeviceDetailTab
│ └───────────────────────────────────┘ │
│ ┌ 设备卡片 ─────────────────────────┐ │
│ │ VGN 键盘                   64%    │ │
│ │ ████████████░░░░░░░░              │ │
│ └───────────────────────────────────┘ │
│ ⚙ ⟳ ⏱ │ 插件图标区                  │ │ ← FlyoutFooterBar
└───────────────────────────────────────┘
```

| 槽位 | 位置 | 内容定位 | 尺寸约束 |
|---|---|---|---|
| `DeviceCardExtension` | 设备卡片内，进度条下方 | 1 行高频操作（DPI 档位、键锁开关） | 高度 ≤ 32px，超出水平截断 |
| `DeviceDetailTab` | 设备卡片内页签栏 | 进详情（灯效、宏、曲线） | 点击 → 推子页 |
| `FlyoutFooterBar` | 面板底部工具条 | 图标按钮，打开插件自有页面 | 24×24 图标 |
| `TrayContextMenu` | 托盘右键菜单 | 单个菜单项 | 文案 ≤ 12 字 |
| `SettingsSection` | 设置窗口分组 | 完整配置界面 | 占满宽度 |

**为什么槽位只有 5 个**：槽位是宿主与插件之间最贵的契约——每加一个就要在布局里留位置、定尺寸、处理空态和拥挤。这 5 个已覆盖「卡片快捷操作 / 详情 / 全局入口 / 菜单 / 设置」全部场景。宁可让插件塞进现有槽位，也不为某个插件新增槽位。

排序规则：`Order` 升序，同值按 `插件 Id` 字典序（保证每次渲染顺序稳定，不会因为加载顺序抖动导致按钮跳位）。

### 6.8 导航：推子页，不开窗

`Nav.PushPage("灯效", view.Root)` 在**面板内**替换内容区，顶部出现返回箭头，栈深上限 3。

不开独立窗口的理由：弹出面板是"看一眼就走"的轻量场景，突然冒出一个新窗口会打断这个心智模型；而且独立窗口要额外处理置顶、失焦关闭、多显示器定位、任务栏条目一堆问题。

需要更宽的界面（灯效矩阵、曲线编辑）时，用 `PushPage` + **面板临时加宽**解决，而不是开新窗口。加宽以当前布局模板的 `defaultWidth` 为基准（`CardList` 360 → 520；`Showcase` / `Sidebar` 本身已 760，一般不用再加），上限受工作区宽度约束（见 12.4）。

从子页返回主列表时，被盖住期间 `OnDeactivatedAsync` 已触发，插件应停掉自己的动画与定时器。

### 6.9 XAML 加载：已知的坑与硬规则

WPF 插件最痛的点是 BAML 资源 URI。`pack://application:,,,/MyPlugin;component/Views/Dpi.xaml` 依赖 `Assembly.Load(byName)` 按程序集名解析，在自定义 `AssemblyLoadContext` 下**必然失败**（默认上下文里没有名为 `MyPlugin` 的程序集）。而 `Application.ResourceAssembly` 是全局单例、只能设一次——等于"只允许一个插件使用 BAML"。

唯一绕法是 `Assembly.LoadFile` 把插件塞进默认上下文，但那样就**失去隔离、失去卸载、依赖版本会互相污染**——和轨道设计的目标直接冲突，不采用。

所以定为硬规则：

| 规则 | 原因 |
|---|---|
| 插件 XAML 以 **EmbeddedResource** 交付，宿主用 `XamlReader.Load(stream, parserContext)` 加载 | 绕开 BAML 与 pack URI |
| 插件 XAML **禁止**引用自身程序集内的图片资源 | 同上，pack URI 必炸；图标改用 `Geometry` 字符串 |
| 颜色一律走 `Theme.*` 的 `DynamicResource`，禁止硬编码 `#RRGGBB` | 主题跟随；诊断面板静态扫描 XAML 并告警 |
| 禁止 `x:Class` 代码后置 | `XamlReader` 不编译，只能生成纯视觉树；需要逻辑就走纯代码构建 |

**实际结论**：L2 优先用**纯代码构建视觉树 + 宿主控件工厂**，XAML 仅用于纯静态布局。插件模板的示例就按这个写，避免后来者踩进去。

### 6.10 失败与降级

| 情况 | 处理 |
|---|---|
| `Factory` 抛异常 | 该槽位替换为「插件加载失败」占位条，其它槽位不受影响，诊断面板记录 |
| 视图内回调抛异常 | `ErrorBoundary` 包住插件的 `Dispatcher.BeginInvoke` 回调，捕获后移除该视图并置灰 |
| `Dispose` 后仍被订阅持有 | 诊断面板告警（弱引用检测），不强制中断 |
| 插件 UI 死锁 | **无法隔离**（同进程同 UI 线程），只能靠 Dispatcher 超时检测记录「疑似卡死」并在下次启动禁用该 UI 贡献 |
| 插件 `minHostVersion` 不匹配 | 不加载 UI，但**驱动仍可用**——界面和驱动解耦 |

最后一条是底线：**UI 失败绝不能影响电量显示**。托盘图标与设备卡片走宿主自己的渲染路径，插件视图是叠加层，坏了就少一块，不会白屏，更不会拖垮托盘。

---

## 7. 设备接入全流程

```
① 枚举         HidEnumerator 列出所有 HID 接口
                 ↓  (VID/PID/UsagePage/ProductString/Serial/Path)
② 去重合并     同一物理设备的多个接口 → 一个 DeviceCandidate
                 ↓
③ 匹配竞标     遍历所有驱动（DLL）+ 所有描述符，取 MatchScore 最高
                 ↓
④ 建会话       ControlChannel（可写）+ ListenChannel（overlapped 只读）
                 ↓  握手 / 初始化
⑤ 能力注册     CapabilityBroker 记录能力 → UI 动态投影
                 ↓
⑥ 调度轮询     PollScheduler 按设备独立节拍 + 退避
                 ↓
⑦ 状态推送     DeviceSnapshot → UI 线程（Dispatcher）
```

### 7.1 热插拔

监听 `WM_DEVICECHANGE` + `RegisterDeviceNotification`（`DBT_DEVNODES_CHANGED` / `DBT_DEVICEARRIVAL` / `DBT_DEVICEREMOVECOMPLETE`）。
事件到达后**防抖 500ms** 再重新枚举——2.4G 接收器上电瞬间会连发多个事件。

单一接口断开会话 ≠ 设备离线（接收器有多个接口）。判定离线要按"该设备的**全部**接口都消失"。

### 7.2 轮询调度

| 场景 | 行为 |
|---|---|
| 空闲 | 电量 60s（可配 30s–30min） |
| 用户打开面板 | 立即全量刷新一次 |
| 用户改设置 | 写入后立即读回校验 |
| 读取超时 | 退避 1→2→5→15→60s，成功即复位 |
| 设备休眠 | 保留旧值 + `IsStale`，退避到 5min |
| 系统休眠/唤醒 | `SystemEvents.PowerModeChanged` → 全部会话重建 |

同一设备的命令用 `SemaphoreSlim(1,1)` **串行化**。并发下发是私有协议最常见的翻车原因（序错乱、丢帧、dongle 卡死）。

---

## 8. MCHOSE / VGN 协议逆向路线

> 这一节是 Track B 驱动的前置工作，按"以真实可写为目标"执行。

### 8.1 第一步：枚举真实接口拓扑

厂商驱动的网页版走 WebHID，浏览器只能拿到**非系统独占**的接口——这本身就是关键线索：
鼠标/键盘的顶层集合被 Windows 独占（`CreateFile` 只能 `dwDesiredAccess=0`，可读属性、不可写），
但同一接收器上必然还有**另一个可写的控制接口**。

```
对 VID/PID 枚举所有 HID 接口，记录：
  UsagePage / Usage / ReportID / InputReportByteLength /
  OutputReportByteLength / FeatureReportByteLength / 是否可写
```

### 8.2 第二步：抓网页驱动的真实报文

三条并行路径，互相验证：

| 路径 | 做法 | 优点 |
|---|---|---|
| WebHID Hook | 在驱动页 console 注入，包裹 `HIDDevice.prototype.sendReport` / `receiveReport`，dump 到剪贴板 | 直接拿到"命令 ↔ 语义"的对应关系 |
| USBPcap + Wireshark | 抓 USB 层，按 endpoint 过滤 | 能看到浏览器看不到的底层时序 |
| Feature Report 全量转储 | 本机对每个 Report ID 做 0x00–0xFF 读取 | 拿到静态配置区（含 DPI / profile 结构） |

**核心手法**：把网页上的一次操作（如拖 DPI 滑条到 1600）对应到一串报文，逐字节 diff——
差异字节就是 DPI 字段。这是最快的定位方式，比盲扫高效一个数量级。

### 8.3 第三步：结构推断与验证

- **DPI**：典型为 `u16le`，单位常为 50 或 100 的倍数；写入后必须**读回校验**（写 800/1600/3200 各读回一次，三次都对才算通）。
- **Windows 键锁定**：通常是 profile 记录里的**一个 bit**，与"游戏模式"共用字段。定位方法：开关各一次，diff profile 区字节。
- **CRC**：若帧尾随两字节随内容变化，先用 `CRC16-CCITT-FALSE`（poly 0x1021 / init 0xFFFF）拟合，覆盖区间用暴力枚举 `[start, end]` 组合验证——参考项目就是这么爆破成功的。

### 8.4 第四步：固化为描述符或驱动

- 单帧可完成 → 写 `device.json`
- 需要握手/序号/profile 结构体 → 写 `McHoseDriver`（Track B）
- 无论哪条，**逆向证据（报文样本）留在 `docs/protocol/` 下**，方便日后固件升级后重新对照

### 8.5 风险与兜底

| 风险 | 兜底 |
|---|---|
| 厂商固件升级改协议 | 描述符带 `firmwareRange`；不匹配时降级为 GenericHidDriver（只读标准电量） |
| 键锁是驱动软件侧行为，非固件功能 | 已实测：N75 PRO 的 Win 锁是**键盘固件状态位**（功能信息块 `[21]`），直接读写即生效，换机器依然有效；只有拿不到固件通道的设备才降级为软件钩子，且 UI 必须标注「模拟实现」 |
| 浏览器也无法写该接口 | 说明必须走厂商 SDK / 内核驱动，此路不通 → 该设备的设置能力标记为不支持 |
| dongle 被厂商软件独占 | 检测占用并提示关闭厂商软件；不做强制抢占 |

---

## 9. 关键模块职责划分

| 模块 | 项目 | 职责 | 明确不做 |
|---|---|---|---|
| 托盘宿主 | `Host.Wpf/Tray` | `Shell_NotifyIcon`、动态图标（电量数字点阵渲染）、tooltip、右键菜单、DPI 感知 | 不碰 HID |
| 弹出面板 | `Host.Wpf/Flyout` | 卡片列表（设备名 / 大号百分比 / 进度条 / 来源与时间）、快捷 DPI 档位、键锁开关 | 不碰 HID，只读快照 |
| 槽位宿主 | `Host.Wpf/Slots` | 槽位容器、Order 排序、空态占位、导航栈（推子页 / 面板加宽）、`ControlFactory` 与主题 token 实现 | 不实现任何插件内容 |
| 插件宿主 | `Core.PluginHost` | 从 `plugins/` 扫描清单、ALC 加载、反射发现 `IUiPlugin` / `IDeviceDriver`、UI 贡献收集、卸载策略（无 UI 热重载 / 带 UI 标记重启） | 不做竞标决策，不做渲染 |
| 皮肤引擎 | `Core.Theming` | `skin.json` 解析与 token 校验、`DynamicResource` 字典合并、换肤原子切换与回滚、跟随系统浅深色、托盘 HICON 绘制与释放、背景模糊图缓存 | 不实现布局（布局在 `themes/`），不含业务逻辑 |
| 设置窗口 | `Host.Wpf/Settings` | 设备管理、插件列表（启用/禁用/重载）、刷新间隔、开机自启、诊断入口 | 不实现协议 |
| 探测工作台 | `Host.Wpf/Probe` | 接口枚举、Feature Report 转储、Report ID 试探、字节扫描、描述符草稿导出 | 不自动写盘到 `plugins/`，需用户确认 |
| 设备注册表 | `Core.Application` | 持有 `DeviceSnapshot` 集合、驱动竞标、热插拔响应、快照推送 | 不发 HID 命令 |
| 轮询调度 | `Core.Application` | 每设备节拍、退避、串行化、电源事件、超时策略 | 不解码报文 |
| 能力代理 | `Core.Application` | 能力集合 → UI 控件描述；设置项读写路由与校验 | 不做设备判断 |
| 会话监管 | `Core.Application` | 会话创建/销毁/重建、异常隔离、`Faulted` 状态机 | 不懂具体协议 |
| 设置持久化 | `Core.Application` | 用户偏好（间隔、自启、插件启用表）→ `%LocalAppData%` | 不存设备协议数据 |
| 抽象契约 | `Core.Abstractions` | 接口、DTO、枚举、描述符模型 | **零实现代码** |
| 插件 UI SDK | `Core.Ui.Sdk` | `IUiPlugin` / `IPluginView` / `IUiHostContext` / `IThemeTokens` / `IControlFactory` / `IUiNavigator` / `ErrorBoundary` | 不含任何设备逻辑与具体控件实现 |
| 描述符引擎 | `Core.Descriptors` | JSON schema 校验、求值器、`DescriptorDriver` 生成 | 不联网、不下载 |
| 通用驱动 | `Drivers.Generic` | 标准 HID Battery Strength（`0x0006/0x0020`）、HID++、常见厂商兜底表 | 不做私有协议 |
| MCHOSE 驱动 | `Drivers.McHose` | A7 V2 Ultra+ 电量/DPI/键锁 | 不含 UI |
| VGN 驱动 | `Drivers.Vgn` | VGN 键盘电量/灯效/键锁 | 不含 UI |
| HID 枚举 | `Transports.Hid` | SetupAPI/hid.dll 枚举、接口合并、路径去重 | 不懂业务语义 |
| 控制通道 | `Transports.Hid` | `WriteFile` / `HidD_SetFeature` / `HidD_GetFeature`，超时与重试 | 不解析内容 |
| 监听通道 | `Transports.Hid` | 独立 `FILE_FLAG_OVERLAPPED` 句柄、事件等待、`CancelIoEx` | 不写入 |
| 设备监听 | `Transports.Hid` | `RegisterDeviceNotification`、防抖、拓扑变化上报 | 不决定重连策略 |
| 诊断 | `Core.Diagnostics` | 报文环形缓冲、录制/回放、异常上报到 UI | 默认关闭，不落盘 |

---

## 10. 传输层设计要点

### 10.1 双句柄模型（最关键的一条）

参考项目的"坑 #1"：同一 HID 句柄上并发「阻塞 `ReadFile`」+「`WriteFile`/IOCTL」会直接杀掉进程，连异常栈都没有。

本设计的解法：

```
ControlChannel  ── 独立句柄，同步写（WriteFile / HidD_SetFeature），短超时
ListenChannel   ── 独立句柄，FILE_FLAG_OVERLAPPED + WaitForSingleObject 超时 + CancelIoEx
```

两条通道生命周期独立。命令-应答配对靠 `ReportId` + 序号 + 时间窗，不靠共享句柄状态。

### 10.2 超时策略

- 单次命令 300ms（可被描述符 `timeoutMs` 覆盖）
- 重试 2 次，间隔 ≥ `quirks.commandGapMs`
- 单设备命令串行；不同设备可并行（不同 dongle 无竞争）
- 取消令牌必须能打断 `WaitForSingleObject`：用 `WaitHandle.WaitAny([ioEvent, ctToken])`

### 10.3 独占与权限

- 系统独占的顶层集合用 `dwDesiredAccess = 0` 打开，只读属性
- 写入必须命中**可写接口**（通常是同一接收器的 configuration collection）
- 不做提权。若某设备确实需要管理员，明确提示，不静默失败

### 10.4 两条用真机换来的结论（2026-10-29）

**（1）「能打开」不等于「能写」，写一次才知道**

`CreateFile(GENERIC_WRITE)` 成功只是个假象：A7 的鼠标集合、`0xFF0B` 厂商页都能打开，但真发
`WriteFile` / `HidD_SetFeature` 时前者被系统拒绝、后者返回 `ERROR_INVALID_FUNCTION(1)`。
可写性判定的唯一可信判据是**真写一帧并检查 Win32 错误码**，见 `WriteProbeResult`。

**（2）`HidP_GetValueCaps` 在托管数组上会踩堆破坏**

按 SDK 原样声明的 `HIDP_VALUE_CAPS`（60 字节，`Marshal.SizeOf` 一致）调用后，进程会随机
消失在别处，退出码 `0xC0000374`（堆破坏），同一条命令有时成功有时段错误。

处置：**运行时不依赖描述符自省**。探测工具可以用它拿到 Report ID 全貌（价值很高，`--caps` 就是靠它
才定位到 `0x2A` / `0x11`），但驱动里写死已验证的 Report ID 与长度，把不确定性留在工具侧。

### 10.5 一个物理设备 = 一张卡片

一只鼠标会暴露 5 个 HID 接口集合（鼠标 / 键盘 / 消费者控制 / 两个厂商页），
按接口建会话会让面板里出现 5 张同名卡片。注册表因此**按物理身份分组**
（`VID + PID + Serial`），组内按驱动分数挑一个接口，同组只建一个会话；
最高分接口打开失败时按分数降级重试。

已知局限：没有序列号的 dongle 无法区分"同型号的第二只"。彻底解决要读容器 ID
（`DEVPKEY_Device_ContainerId`），属 SetupAPI 依赖，等真有人插两只同型号再上。

---

## 11. 稳定性红线（必须写进代码评审清单）

1. 双句柄分离，禁止共享句柄并发读写。
2. 退出路径：`Application.Shutdown` → 注销托盘图标 → 释放会话 → 释放 Mutex。**缺一步就是僵尸进程 + 之后再也起不来**。
3. 托盘图标更新：`NIM_MODIFY` 失败（图标被删过）时**必须 `NIM_ADD`**，不能只 modify。
4. DPI 感知：进程声明 Per-Monitor V2，否则图标被系统缩放糊掉。
5. UI 线程不阻塞：所有 HID 操作 `Task.Run` + 快照推送，禁止 `.Result` / `.Wait()`。
6. 单实例锁被占但进程已死（异常退出）：检测并清理，不要直接退出。
7. 不做"自动纠正用户设置"：只读开机自启的真实状态，绝不因为读到不一致就替用户增删启动项。
8. 描述符加载失败必须可见：诊断面板列出失败原因，禁止静默跳过。
9. 插件视图**懒实例化**：面板未打开时不得构造任何插件控件，否则空闲 CPU/内存承诺作废。
10. 插件视图必须在 UI 线程构造，且实现 `Dispose` 解除全部订阅；诊断面板负责点名泄漏源。
11. 插件 UI 崩溃/卡死**不得影响电量显示**：槽位是叠加层，坏一块不能白屏。UI 加载失败时驱动仍照常工作。
12. 插件内禁止硬编码颜色与 pack URI 图片资源：主题跟随与 ALC 隔离都靠这条活着。
13. 换肤必须是**原子且可回滚**：token 全部校验通过才应用，失败保持当前皮肤，禁止白屏。
14. 皮肤只换外观不换语义：槽位位置可随布局模板变，功能不许藏。
15. 托盘 `HICON` 用完必须 `DestroyIcon`；背景模糊必须预生成缓存，禁止在窗口根节点挂 `BlurEffect`。
16. 可写性判定必须真写一帧并检查 Win32 错误码，禁止用「`CreateFile` 成功」代替。
17. 一个物理设备只建一个会话（按 `VID+PID+Serial` 分组），否则面板重复 N 份、命令并发打同一 dongle。
18. 驱动不得在运行时调用 `HidP_GetValueCaps` 做自省（实测会堆破坏）；Report ID 必须是验证过的常量。

---

## 12. UI 皮肤与布局模板系统

### 12.0 先拆解参考图：皮肤其实是三个维度

六张参考图放在一起，差异不在"配色"，而在三个互不相干的维度：

| 参考 | 布局结构 | 视觉 token | 装饰层 | 电量样式 |
|---|---|---|---|---|
| 图1 外设电量 | 纵向卡片列表 | 深底 + 高对比文字 | 无 | 条形 |
| 图2 任务栏 | —（托盘图标） | 深底 | 发光圆环 | 环形（托盘） |
| 图3 Nexus | 双列大卡 | 浅底 + 大留白 | 产品实拍 + 巨型水印字 | 纯文字 |
| 图4 ROG 控制中心 | 左设备 + 右设置 | 暗紫赛博渐变 | 产品图 + 光晕 | 环形 |
| 图5 我的设备电量 | 双列卡 + 底部开关组 | 深底 + 青绿强调 | 图标 | 条形（细） |
| 图6 电量坞 | 左侧栏 + 右侧详情 | 毛玻璃漫彩 | 壁纸背景 | 环形 |

**结论**：如果只做"配色皮肤"，图 3/4/6 的结构永远做不出来；如果让每套皮肤自带完整布局，就是六套布局代码。正确做法是拆成三层。

### 12.1 三层皮肤模型

| 层 | 是什么 | 载体 | 热切换 | 成本 |
|---|---|---|---|---|
| **S0 Token 皮肤** | 颜色 / 圆角 / 字号 / 密度 / 背景材质 / 电量样式 | `skin.json` | ✅ 零重启 | 改 JSON |
| **S1 布局模板** | 面板骨架（卡片列表 / 展示 / 控制台 / 侧边栏） | 宿主内置 4 个 | ✅ | 选一个枚举值 |
| **S2 皮肤插件** | 完全自定义外壳与布局 | `Skin.dll` | ❌ 需重启 | 写代码 |

**绝大多数皮肤只需要 S0。** 图 1、图 3、图 5 全部是 S0 + 不同 S1 模板，零代码。只有图 6 那种"壁纸 + 毛玻璃 + 侧边栏 + 趋势图"的整块重构才考虑 S2——而它其实也能靠 S0 的背景材质 + S1 的 Sidebar 模板覆盖，先别急着上 S2。

### 12.2 S0：Token 规范

`skins/<id>/skin.json`：

```jsonc
{
  "schema": "skin-v1",
  "id": "minimal.dark",
  "name": "极简深色",
  "author": "local",
  "template": "CardList",          // S1 布局模板
  "density": "comfortable",        // compact | comfortable

  "tokens": {
    "surface":        "#1C1C1E",
    "surfaceRaised":  "#232325",
    "surfaceSunken":  "#141416",
    "overlay":        "#CC000000",
    "textPrimary":    "#F5F5F7",
    "textSecondary":  "#8E8E93",
    "textTertiary":   "#5A5A5E",
    "accent":         "#34C759",
    "onAccent":       "#0A0A0A",
    "success":        "#34C759",
    "warning":        "#FF9F0A",
    "danger":         "#FF453A",
    "border":         "#2C2C2E"
  },

  "shape":     { "radiusCard": 14, "radiusControl": 8, "borderWidth": 1 },
  "typography":{ "fontSans": "Segoe UI Variable, Segoe UI",
                 "fontMono": "Cascadia Mono",
                 "scale": 1.0, "displaySize": 44 },
  "metrics":   { "cardPadding": 16, "gap": 12, "rowHeight": 28 },

  "battery": {
    "style": "bar",                // bar | ring | text
    "thickness": 6,
    "trackOpacity": 0.12,
    "colorMode": "state",          // state | accent | gradient
    "gradient": ["#FF9F0A", "#34C759"]
  },

  "tray": { "style": "ring", "thickness": 2.5, "colorMode": "state" },

  "surfaceMaterial": {
    "kind": "solid"                // solid | gradient | image | acrylic | mica
  },

  "decoration": { "watermark": null, "productImage": false },

  "followsSystem": { "light": null, "dark": "minimal.dark" }
}
```

#### Token 必须语义化，不能色相化

这是整套系统能不能扩的前提。写成 `blue600` / `orange300`，换到图 4（橙红强调）时语义就错位了——它要的不是"蓝"，是"强调色"。

| 语义 key | 用途 | 禁用写法 |
|---|---|---|
| `accent` | 强调色（电量条、选中态、主按钮） | ~~primaryBlue~~ |
| `surface` / `surfaceRaised` / `surfaceSunken` | 三层底色 | ~~bgDark / bgCard~~ |
| `textPrimary` / `textSecondary` / `textTertiary` | 三级文字 | ~~textWhite / textGray~~ |
| `border` / `overlay` | 描边 / 遮罩 | — |
| `success` / `warning` / `danger` | 状态（低电红、正常绿） | ~~green / red~~ |

缺 key 时用内置默认值补齐（不报错），但**多出的未知 key 要报警**——防止拼写错误静默失效。

### 12.3 电量样式的三种渲染器

六张图里出现了三种：条形（图1/5）、环形（图2/4/6）、纯大字（图3）。这是**同一个 token 驱动三个渲染器**，不是三套皮肤各写一遍：

| 样式 | 实现要点 |
|---|---|
| `bar` | `Rectangle` + 圆角裁切；进度用宽度百分比 |
| `ring` | `Path` + `ArcSegment` 描边，**圆角端点靠 `StrokeStartLineCap/EndLineCap=Round`**；不要用 `Ellipse` + `StrokeDashArray`，那样做不出圆头 |
| `text` | 只显示数字 + 小号 `%`，靠字号建立层级 |

`colorMode` 三档：

- `state` —— ≥60 用 `success`，30–59 用 `warning`，<30 用 `danger`（图1 的橙黄/绿就是这个）
- `accent` —— 全部跟随 `accent`，状态只靠文案表达（图4 的橙红环）
- `gradient` —— 低电→高电线性渐变（图2 托盘环）

### 12.4 S1：四个内置布局模板

| 模板 | 骨架 | 默认尺寸 | 对应参考 |
|---|---|---|---|
| `CardList` | 纵向卡片流，每卡：名称 / 大字百分比 / 进度条 / 元信息 | 380 × auto | 图1、图5 |
| `Showcase` | 双列大卡，卡片内左侧产品图 + 右侧巨型水印字 | 760 × 520 | 图3 |
| `Console` | 左栏设备预览 + 圆环电量，右栏设置项常驻 | 780 × 560 | 图4 |
| `Sidebar` | 左侧设备列表（含在线数），右侧详情 + 趋势 | 760 × 560 | 图6 |

**面板尺寸由模板声明**，宿主只做工作区裁剪：

```
defaultWidth = 模板声明值
clamp(320, min(defaultWidth, 工作区宽 - 48))
height: auto，超过 工作区高 - 80 则内部滚动
```

这修正了 6.7 里"面板固定 360、最多加宽到 520"的说法——那条只对 `CardList` 成立，`Showcase` / `Console` / `Sidebar` 天生更宽。

#### 模板只决定骨架与槽位落点

插件槽位在四个模板里的位置必须预先定义好，否则插件不知道自己会被塞到哪：

| 槽位 | CardList | Showcase | Console | Sidebar |
|---|---|---|---|---|
| `DeviceCardExtension` | 卡片内进度条下方 | 卡片底部元信息行上方 | 左栏电量环下方 | 右栏标题下方 |
| `DeviceDetailTab` | 卡片内页签栏 | 卡片右上角图标组 | 右栏顶部标签条 | 右栏顶部标签条 |
| `FlyoutFooterBar` | 面板底部 | 面板底部 | 面板底部 | 左侧栏底部 |
| `TrayContextMenu` | 右键菜单 | 右键菜单 | 右键菜单 | 右键菜单 |
| `SettingsSection` | 设置窗口 | 设置窗口 | 设置窗口 | 设置窗口 |

`Console` 比较特殊：它把 `DeviceDetailTab` **常驻显示**（图 4 右栏就是设置项），而其他模板是"点按钮才推子页"。这个行为由模板声明 `detailMode: "resident" | "push"`，插件不用感知。

### 12.5 S2：皮肤插件

真需要独特外壳时才用：

```csharp
public interface ISkinProvider
{
    SkinManifest Manifest { get; }
    ResourceDictionary CreateTokens();                    // 向宿主提供 token 字典
    FrameworkElement? CreateShell(ISkinHostContext host); // 可选：完全自定义外壳
}

public interface ISkinHostContext
{
    IReadOnlyList<DeviceSnapshot> Devices { get; }
    FrameworkElement SlotHost { get; }   // 宿主已排好序的槽位容器
    IControlFactory Controls { get; }
}
```

两条边界：

1. **皮肤插件只能摆位置，不能重写卡片内部**。它拿到的是宿主渲染好的 `SlotHost`，可以放到任意容器里、加背景、加装饰，但不能把电量卡片拆了重画——否则每个皮肤都要重新实现一遍电量渲染，插件槽位也得跟着重写。
2. **需重启**（同 L2 理由：WPF 可视化树引用收不回）。

### 12.6 换肤的运行时机制

```
启动 → 读用户配置的 skin id → 解析 skin.json → 校验补全 token
     → 合并进 Application.Resources 的固定 key 集合
     → 所有控件只引用 {DynamicResource Skin.Surface} 这类固定 key
```

换肤 = **替换那个字典的 values**，不重建控件树。

四个实现要点：

1. **Brush 必须 `Freeze()`**。冻结后跨线程安全、免去变更通知开销，能省掉大量布局抖动。
2. **key 固定、值可变**。如果 key 随皮肤变（`Skin.Minimal.Surface`），换肤就等于重新绑定整棵树，必然闪。
3. **切肤闪烁**：`DynamicResource` 是逐元素更新的，会出现"一半新一半旧"的瞬间。低成本解法是在切换前把面板内容截成 `RenderTargetBitmap` 盖在最上层，资源替换完在下一帧移除——用户看到的是"一次性换完"，而不是逐元素刷新。
4. **跟随系统浅/深色**：`skin.json` 的 `followsSystem` 声明配对皮肤；监听 `SystemEvents.UserPreferenceChanged`，读注册表 `HKCU\...\Themes\Personalize\AppsUseLightTheme` 判定当前模式。

### 12.7 背景材质：三种方案的真实成本

| 材质 | 实现 | 成本 | 坑 |
|---|---|---|---|
| 纯色 / 渐变 | `SolidColorBrush` / `LinearGradientBrush` | 零 | 无 |
| 图片 | `ImageBrush` + 预生成模糊图 | 中 | **`BlurEffect` 作用于整个视觉树极慢**，绝不能在窗口根节点上挂 |
| 亚克力 | Win10：`SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND`<br>Win11：`DwmSetWindowAttribute` + `DWMWA_SYSTEMBACKDROP_TYPE` | 低 | Win10 拖动窗口时掉帧（已知缺陷），拖动期间临时降级为纯色 |
| Mica | Win11 22H2+ 同上 | 低 | Win10 不可用，静默降级纯色 |

**图片模糊的正确做法**：把背景图先缩到 1/8 尺寸 → `BlurEffect(Radius 8~12)` → 绘制时 `Stretch` 放大。
视觉等效，开销降两个数量级。结果按 `(图片 hash + 目标尺寸)` 缓存到 `%LocalAppData%\2.4GHUB\cache\`，避免每次开面板重算。

图 6 那种"粉色壁纸 + 毛玻璃卡片"就是：`surfaceMaterial.kind = "image"` + 卡片 `surface` 用 `#33FFFFFF` 半透明 + 背景预模糊。

### 12.8 托盘图标也是皮肤的一部分

图 2 显示托盘图标本身就是环形电量。但托盘不用 WPF 渲染——必须是 `HICON`。所以托盘有独立的一套 token：

| `tray.style` | 效果 |
|---|---|
| `ring` | 环形进度，中心留空（图2） |
| `text` | 数字点阵，图标直接是百分比 |
| `bar` | 竖直能量条 |

实现要点：

- GDI+ 绘制到 `Bitmap` → `Icon.FromHandle` → `Shell_NotifyIcon` → **旧的 HICON 必须 `DestroyIcon`**，否则 60s 一刷就是一小时 60 个 GDI 句柄泄漏，一天后系统资源耗尽
- 高 DPI 准备多尺寸：`GetSystemMetrics(SM_CXSMICON)` 取实际值，16 / 20 / 24 / 32 都生成
- 颜色跟随皮肤 `accent` + `tray.colorMode`

### 12.9 皮肤包格式与失败回退

```
skins/
  default-dark/          ← 内置，编译进宿主，永不失败
  minimal-dark/          ← 用户皮肤
    skin.json
    bg.jpg
    preview.png          ← 皮肤选择器里显示
  nexus-light/
    skin.json
```

加载规则：

- `schema: skin-v1`，主版本不兼容 → 跳过并提示
- 解析失败 / 缺关键字段 → **保持当前皮肤不变**，绝不白屏
- 内置 `default-dark` 是硬编码兜底，任何情况下都能启动
- 换肤是**原子操作**：全部 token 校验通过才应用，中途失败回滚

### 12.10 硬规则（进评审清单）

- token 语义化命名，禁止 `blue600` 这类色相名
- 宿主控件、插件界面一律 `DynamicResource`，禁止硬编码 `#RRGGBB`
- 皮肤不得改变交互语义：槽位位置可随模板变，但功能不能藏起来
- 密度靠 token 乘数实现，**不得写两套布局**
- 皮肤加载失败必须可见且可回退
- 面板关闭时不渲染背景与模糊（空闲零占用的承诺同样适用于皮肤）
- 托盘 HICON 用完必须 `DestroyIcon`

### 12.11 用这套模型复现参考图（验证可行性）

图 1（深色卡片列表）：

```jsonc
{ "id": "minimal.dark", "template": "CardList", "density": "comfortable",
  "tokens": { "surface": "#1C1C1E", "surfaceRaised": "#232325",
              "textPrimary": "#F5F5F7", "textSecondary": "#8E8E93",
              "border": "#2C2C2E", "accent": "#34C759" },
  "battery": { "style": "bar", "thickness": 6, "colorMode": "state" } }
```

图 3（Nexus 浅色展示）：

```jsonc
{ "id": "nexus.light", "template": "Showcase", "density": "comfortable",
  "tokens": { "surface": "#FFFFFF", "surfaceRaised": "#FAFAFA",
              "textPrimary": "#1A1A1A", "textSecondary": "#8A8A8E",
              "accent": "#00C853" },
  "typography": { "displaySize": 56 },
  "battery": { "style": "text", "colorMode": "state" },
  "decoration": { "productImage": true,
                  "watermark": { "text": "MOUSE", "opacity": 0.05, "position": "right" } } }
```

两份 JSON，零代码，覆盖两种完全不同的视觉。这就是"皮肤可扩展"的实际含义。

---

## 13. 目录结构

```
2.4GHUB/
├─ src/
│  ├─ Host.Wpf/                    # WPF 托盘 + 面板 + 设置 + 探测台
│  ├─ Core.Abstractions/           # 接口与 DTO（零实现）
│  ├─ Core.Application/            # 注册表、调度、能力代理、持久化
│  ├─ Core.Descriptors/            # JSON schema + 求值器
│  ├─ Core.Ui.Sdk/                 # 插件 UI 契约：IUiPlugin / IUiHostContext / 槽位模型
│  ├─ Core.PluginHost/             # 插件发现、ALC 加载、UI 贡献收集、卸载策略
│  ├─ Core.Theming/                # 皮肤解析、token 校验、换肤原子切换、托盘 HICON 绘制
│  ├─ Core.Diagnostics/            # 报文录制/回放
│  ├─ Transports.Hid/              # 枚举、双通道、设备监听
│  └─ Drivers/
│     ├─ Drivers.Generic/          # 标准 HID / HID++ 兜底
│     ├─ Drivers.McHose/
│     └─ Drivers.Vgn/
├─ plugins/                        # 运行时插件目录（描述符/DLL）
│  └─ .template/
├─ skins/                          # 皮肤目录（skin.json + 背景图 + preview）
│  ├─ default-dark/                # 内置兜底，通过 EmbeddedResource 编进宿主
│  └─ .template/
├─ themes/                         # 布局模板（宿主内置，S1 的四个实现）
│  ├─ CardList.xaml
│  ├─ Showcase.xaml
│  ├─ Console.xaml
│  └─ Sidebar.xaml
├─ docs/
│  ├─ DESIGN.md                    # 本文档
│  └─ protocol/                    # 逆向证据：报文样本、字段推断
└─ tests/
   ├─ DescriptorEngine.Tests/      # 求值器单测（纯数据，最好测）
   └─ Transport.Tests/             # 用假 transport 测调度与超时
```

---

## 14. 实施里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| M0 | 传输层 + 枚举 + 探测工作台 | 能列出本机所有 HID 接口、转储 Feature Report |
| M1 | 核心抽象 + 通用驱动 + 托盘 | 标准 HID 设备能显示电量，托盘图标正常，退出无残留 |
| M1.5 | 皮肤系统 S0 + `CardList` 模板 | ✅ S0 完成：`Core.Theming` 语义 Token + 内置浅/深色 + `skins/<id>/skin.json` 离线加载，切肤只换资源字典（热切换画面与直接启动该皮肤**像素级一致**）。⚠️ `CardList` 仍是现有面板未做布局抽象；托盘环形图标未跟随状态色 |
| M2 | 描述符引擎 + 面板 | 一份 JSON 就能接入设备，卡片自动渲染 |
| M2.5 | 槽位系统 + L0/L1 声明式 UI | 描述符里的 `ui.blocks` 能在卡片扩展区与详情页签里渲染出来 |
| M2.6 | 其余三个布局模板 + 背景材质 | `Showcase` / `Console` / `Sidebar` 可切换；图片/亚克力背景性能达标 |
| M3 | MCHOSE 协议逆向（电量） | ✅ 2026-10-29 完成：A7 V2 Ultra+ 读到 97%，三次一致 |
| M4 | MCHOSE 写入能力（DPI / 键锁） | 改 DPI 后读回一致；键锁开关生效 |
| M4.5 | UI SDK（L2 自定义视图） | 一个示例插件在 `DeviceDetailTab` 里推子页，面板内导航正常 |
| M5 | VGN 键盘接入 | ✅ 电量 + 固件设置 14 项（灯效/轮询率/休眠/低延迟/六键无冲/Win 锁/Mac 等）全部可写可回读；**DPI / Win 锁 / 灯效已实机验证生效**（详见 `docs/protocol/2026-10-29-vgn-keyboard-protocol.md` §4.6）；改键（ReadKey/ChangeKeys）未做 |
| M6 | 插件热重载 + 诊断完善 | 无 UI 驱动改代码无需重启；带 UI 插件正确提示"需重启" |

---

## 15. 待验证的前提（风险清单）

1. ~~**A7 V2 Ultra+ 是否存在可写控制接口**~~ —— ✅ 已确认：`0xFF01`（Feature `0x11`）可写可用；`0xFF0B` 有 OUT 报表但 `SetFeature` 一律返回 `ERROR_INVALID_FUNCTION`。详见 `docs/protocol/2026-10-29-mchose-protocol.md`。
2. ~~**Windows 键锁定是固件态还是软件态**~~ —— ✅ 已确认是**固件态**：N75 PRO 把 Win 锁存在功能信息块第 21 字节，读写 `0xFF1C` 即可，实测 0→1→0 全部可回读。软件钩子（`WinKeyLockService`）降级为诊断工具，不再进 UI。
3. ~~**VGN 键盘的电量是否由 dongle 上报**~~ —— ✅ 已打通：`0xFF1C` 上发 `04 00 00 1A 06 00 00 00`（命令 26），应答数据段 `[0]`=电量%、`[1]`=充电。实测 55% 三次一致。**不需要握手**。详见 `docs/protocol/2026-10-29-vgn-keyboard-protocol.md`。
4. ~~**两台设备是否共用一个接收器**~~ —— ✅ 已排除，各 `3837:100B` / `320F:5088` 独立。
5. **WPF 插件 UI 没有崩溃隔离** —— 同进程、同 UI 线程，插件死锁只能检测不能隔离。这是「自用离线、可信代码」换来的代价；若将来要分发给别人，UI 必须改成子进程承载。
6. **带 UI 的插件无法热卸载** —— 已按分级策略处理（提示"下次启动不加载"），但用户仍可能期望"点一下立刻消失"，UI 文案必须写清楚。
7. **皮肤数量会爆炸维护成本** —— 每多一套皮肤就多一份要跟着布局模板升级的 token 集。缓解：皮肤只允许声明 token，不得写布局；新增 token 必须给默认值，老皮肤自动兼容。
8. **Win10 亚克力拖动掉帧** —— 图 6 那种毛玻璃在 Win10 上体验会打折。兜底是拖动期间降级纯色，需要实测确认降级阈值。
9. **产品实拍图（图 3/4/6）需要素材** —— 皮肤要"好看"就得有设备干净抠图。程序不联网，所以素材得随皮肤包分发或由用户提供；不做在线图库。
