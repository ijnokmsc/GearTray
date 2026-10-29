# VGN N75 PRO 2.4G 键盘协议（逆向记录 · 已打通）

设备：`VID_320F&PID_5088` · `VGN N75 PRO 2.4G Dongle`
记录时间：2026-10-29 · 状态：**电量与 Win 锁均已实测通过**

---

## 0. 结论先行

| 能力 | 命令 | 请求 | 应答取值 |
|---|---|---|---|
| 电量 | `26 (0x1A)` GetBatterLevel | `04 00 00 1A 06 00 00 00` | 数据 `[0]`=电量%、`[1]`=充电标志 |
| 版本 | `3 (0x03)` GetVersion | `04 00 00 03 18 00 00 00` | `data[4..6]`，VHUB 拼成 `{data[5]:X2}{data[4]:X2}` |
| 功能信息 | `5 (0x05)` GetFunctionInformation | 先 offset=0 再 offset=24，各 24 字节 | 共 48 字节，见第 4 节字段表 |
| 写功能信息 | `6 (0x06)` SetFunctionInformation | 同一帧头 + 24 字节数据，两块都要写 | — |

实测值：**55%**（连续 3 次一致）；功能信息块 **14 个可调项**逐个写入并回读确认（灯效模式/亮度/灯速/方向/
混色/静态色/休眠/方向键交换/六键无冲/Win 锁/轮询率/Mac 模式/超低延迟/低延迟），写完均还原原值。

其中 **DPI、Win 键锁、灯效三类已由用户实机确认有物理效果**（鼠标速度变化、Win 键被屏蔽、
键盘实际亮起）。其余项目只有回读确认，逐项状态见 §4.6。

---

## 1. 接口拓扑（实测）

| # | UsagePage | IN | OUT | FEAT | 用途 |
|---|---|---|---|---|---|
| 1 | 0x0001 | 9 | 2 | 65 | 标准键盘 + LED 输出 |
| 2 | 0x0001 | 16 | 0 | 0 | 标准键盘 |
| 3 | 0x0001 | 2 | 0 | 0 | 系统控制 |
| 4 | 0x000C | 3 | 0 | 0 | 消费者控制 |
| **5** | **0xFF1C** | **64** | **64** | 0 | **厂商通道（唯一）** |
| 6 | 0x0001 | 33 | 33 | 0 | 未知 |
| 7 | 0x0001 | 8 | 0 | 0 | 未知 |

厂商通道**没有 Feature Report**，只能走 Output 写 + Input 读。写 Report ID 必须是 **4**：

- ID 0 → `ERROR_INVALID_PARAMETER (87)`
- ID 1 → `ERROR_INVALID_FUNCTION (1)`
- **ID 4 → 成功**

---

## 2. 三套协议，别再用错

这只键盘上能查到三套互不兼容的帧格式，混用就是「写成功但回 0xFF」：

| | 帧格式 | 出处 | 本机适用性 |
|---|---|---|---|
| SignalRGB 灯效 | `[04][校验和][0B/12][参数]` | 第三方灯效同步 | 仅灯效 |
| ~~网页驱动配置~~ | ~~`[04][55][cmd][参数]`~~ | `drive.vgnlab.com.cn` 旧版 | **❌ 只回 `04 55 03 FF`** |
| **VHUB 桌面端** | `[04][00][00][cmd][len][offset][00][00][data…]` | `VGN VHUB/resources/app.asar` | **✅ 本文件实现** |

> 教训：第 2 套是**旧版/别的产品线**的协议。当初按它发 `04 55 03`（DeviceOnLine），
> 设备每次都回 `04 55 03 FF …`，`FF` 位置还随次变化，误判成「加密会话 / 事务包裹 / 键盘休眠」。
> 实际上它只是不认识这个帧头。**判断协议时要先确认「这份代码对应的是不是这台设备」**，
> 而不是把「不认识的命令」解释成「需要握手」。

---

## 3. 帧结构（VHUB 协议）

Output Report ID = `0x04`，缓冲 64 字节，尾部补零。原始 buffer 下标：

```
[0]  0x04        Report ID
[1]  0x00        ┐
[2]  0x00        │
[3]  cmd         │ 7 字节协议头
[4]  len         │
[5]  offset      │
[6]  0x00        │
[7]  0x00        ┘
[8…] data        —— 最长 56 字节（64 - 8）
```

**应答 = 头部原样回显 + 数据段**，数据同样从 `[8]` 开始。

VHUB 代码里 `receivedData` 是 WebHID 的 `event.data`（**不含 Report ID**），
所以它的 `payload[2]=cmd`、`payload[3]=len`、`payload[4]=offset`、`payload[7..]=data`
落到我们的原始 buffer 上就是 `[3]`/`[4]`/`[5]`/`[8..]` —— **全部 +1**。
这个差一位的坑本项目已经栽过三次（MCHOSE 电量、V9 耳机、这里），写驱动时一律以原始 buffer 为准。

### 3.1 应答匹配

接收器会把请求原样回显一遍。**只比 cmd 会把回显当应答**，必须同时比 `offset`：

```csharp
if (report[3] != command || report[5] != offset) continue;
```

### 3.2 不需要握手

和 MCHOSE（先 `DeviceOnLine` 再读电量）不同，这里**直接发 `26` 就有回包**。
键盘休眠时接收器的表现是「不回包」，不是回一个失败标志 ——
所以驱动里把「无应答」映射成 `DeviceHealth.Asleep` + 保留旧值标 Stale。

---

## 4. 功能信息块（所有可调项都在这）

Win 锁不是独立的命令，它是**功能信息块里的一个字节**。灯效、轮询率、休眠、低延迟全在同一块里，
所以能力扩展几乎没有额外协议成本 —— 只是换下标。

### 4.1 字段全表（VHUB 下标，全部实测可写可回读）

| 下标 | 含义 | 编码 | 取值 | 本机实测初值 |
|---|---|---|---|---|
| 1 | 灯效模式 | Byte | 1–18 动态 / 19 自定义 / 20 关闭 / 254 音乐律动 | 20 关闭 |
| 2 | 亮度 | Byte | 0 最亮 … 4 最暗 | 4 |
| 3 | 灯速 | Byte | 0 最快 … 4 最慢 | 2 |
| 4 | 灯效方向 | Byte | 0 左→右 / 1 右→左 | 0 |
| 5 | 混色 chroma | Byte | 0 关 / 1 开 | 1 |
| 6–8 | 静态色 RGB | 3 字节 | RR GG BB | FF FF FF |
| 13–14 | 休眠时间 | UInt16 小端 | 秒；`FFFF` = 从不。档位 60/180/300/600/1800 | 600（10 分钟） |
| 19 | 方向键交换 | Byte | 0 关 / 1 开（WASD ↔ 方向键） | 0 |
| 20 | 六键无冲 | Byte | **0 = 开**，非 0 = 关 | 255（关） |
| **21** | **Win 键锁** | Byte | 0 关 / 1 开 | 0 |
| 22 | 轮询率 | Byte | 0=1000Hz / 1=500 / 2=250 / 3=125 | 1（500Hz） |
| 27 | Mac 模式 | Byte | 0 关 / 1 开 | 0 |
| 28 | 灯效开关 | Byte | **0 = 开**，1 = 关 | 0 |
| 33 | 超低延迟 | Byte | 0 关 / 1 开 | 0 |
| 34 | 低延迟 | Byte | 0x00 关 / 0xFF 开 | 0 |

灯效模式中文名取自 VHUB 简体语言包 `weiShengLightData`：
1 随波逐流 / 2 彩云纷飞 / 3 峰回路转 / 4 光之审判 / 5 呼吸 / 6 常亮 / 7 踏雪无痕 / 8 泛起涟漪 /
9 奔逸绝尘 / 10 暗流涌动 / 11 百花争艳 / 12 流星赶月 / 13 大鹏展翅 / 14 川流不息 / 15 落雨纷纷 /
16 左右逢源 / 17 众志成城 / 18 速度激情 / 19 自定义 / 20 关闭 / 254 音乐律动。

### 4.2 读写流程

```
读：cmd 5, offset 0  → data[0..23]
    cmd 5, offset 24 → data[24..47]      拼成 48 字节块
写：改字段后
    cmd 6, offset 0  + buff[0..23]
    cmd 6, offset 24 + buff[24..47]
```

**必须整块回写**：设备没有「只改一个字段」的命令，单独发某一位会把其余灯效/按键配置一起写成 0。

写后必须 `Sleep(150)` 再回读 —— 固件落盘有延迟，立刻读会拿到旧值。
回读不一致就当写入失败，并把旧值整块写回去，绝不在 UI 上留「点过了但没生效」的假象。

### 4.3 回读校验的坑：开关类不能精确比对

实测：把六键无冲写成 `1`（关），设备回读是 `1`；但出厂状态下这一位是 **`0xFF`**，语义同样是「关」。

也就是说同一语义在这颗固件上可以有多个表示。**开关类字段只校验「零 / 非零」**，
否则一次成功的写入会被判成失败，进而触发无谓回滚。

```csharp
// FieldSpec.MatchesReadBack
Kind == Toggle ? (actual == 0) == (expected == 0) : actual == expected
```

### 4.4 反直觉的位

两处 `0 = 开`：`[20]` 六键无冲、`[28]` 灯效开关。
代码里不要写 `value != 0` 当「开」，字段表把「开」对应的原始值放在选项第一位，UI 从表里取语义。

### 4.5 连带改动

实测 14 项逐个写入，只有目标字节变化，没有出现「改 A 连带改 B」的副作用
（VHUB 代码里换灯效模式会顺手改速度/方向，但那是它自己的逻辑，不是固件行为）。
探测器的 `--vgn-ws-set` 会打印 collateral changes，若哪天固件变了能立刻看见。

### 4.6 确认状态：回读 ≠ 生效

「设备回读了新值」和「用户看到了物理效果」是两件事，必须分开记，否则文档会虚高。

| 项 | 回读确认 | 实机确认（用户肉眼/触感） |
|---|---|---|
| 电量 | ✅ 55% → 42% 多次一致 | ✅ 面板数字随使用下降 |
| DPI 六档 | ✅ 切档读回一致 | ✅ **鼠标速度实际变化** |
| Win 键锁 | ✅ 0→1→0 回读一致 | ✅ **Win 键被键盘固件屏蔽，WIN 指示灯亮** |
| 灯效（模式/亮度/灯速/方向/静态色/混色/开关） | ✅ 逐项写入回读一致 | ✅ **键盘实际亮起、颜色与模式变化可见** |
| 轮询率 | ✅ 500↔250 回读一致 | ⬜ 未验（1000Hz 有掉线风险，未尝试） |
| 六键无冲 | ✅ 255→0→1 回读一致 | ⬜ 未验（需按键冲突场景） |
| 超低延迟 / 低延迟 | ✅ 回读一致 | ⬜ 未验（无可感观测量） |
| 方向键交换 | ✅ 0→1→0 回读一致 | ⬜ 未验（写了会真的换键位，测试者需知情） |
| 休眠时间 | ✅ 600↔180 回读一致 | ⬜ 未验（需等待设备实际休眠） |
| Mac 模式 | ✅ 0→1→0 回读一致 | ⬜ 未验 |

结论：**已实机确认的是 DPI、Win 锁、灯效三类**，其余是「设备接受了写入且回读一致」，
不代表物理生效。往后再动这些位，不要拿「之前测过」当依据。

---

## 5. 命令行表（`WsCmd`，全部 14 个）

| 值 | 名称 | 说明 |
|---|---|---|
| 1 | QuickCommunicationStarts | 快速通信开始（代码中未直接调用） |
| 2 | QuickCommunicationEnd | 快速通信结束 |
| 3 | GetVersion | 版本号 |
| 5 | GetFunctionInformation | 读功能信息（Win 锁在这） |
| 6 | SetFunctionInformation | 写功能信息 |
| 8 | ReadKey | 读按键层 |
| 9 | ChangeKeys | 改键 |
| 10 | GetCustomLightEffect | 读自定义灯效 |
| 11 | SetCustomLightEffect | 写自定义灯效 |
| 13 | RestoreFactorySettings | 恢复出厂 |
| 26 | GetBatterLevel | **电量** |
| 27 | GetLightEffectMatrix | 灯效矩阵 |
| 38 | FnReadKey | 读 Fn 层 |
| 39 | FnChangeKeys | 改 Fn 层 |

---

## 6. 协议是怎么拿到的（可复用方法）

1. `app.asar` 152MB，主进程被 bytenode 成 `dist/main/index.jsc`（无 ASCII 字符串），**不要从那里啃**。
2. 渲染端是明文 JS：`dist/renderer/assets/*.js`。用 `tools/asarx/asar.mjs` 直接读 asar，无需 npm 依赖：
   ```bash
   node tools/asarx/asar.mjs "<path>/app.asar" ls "dist/renderer/assets/"
   node tools/asarx/asar.mjs "<path>/app.asar" grep "getBatteryStatus" "dist/renderer/"
   node tools/asarx/asar.mjs "<path>/app.asar" x "<entry>" out.js
   ```
3. `grep DeviceOnLine` 命中的是**鼠标**（`JmServe`，19 字节 + CRC，seed 85）。真正决定用哪个类的是
   `HidServe.requestDevice()` 里按 `config.customDeviceType` 分派：
   `"weisheng"` → `new WsServe(device)`。**从分派表反查实现类，比按名字猜快十倍。**
4. 设备清单在 `useDeviceInfoStore().deviceList`：`{vendorId, productId, path, reportId, customDeviceType, customDeviceController}`。
   本机条目：`{12815, 20616, "mi_01&col01", receiver: true, "weisheng", reportId: 4}`。

---

## 7. 复现命令

```bash
cd src/Tools.HidProbe/bin/Debug/net10.0-windows

hidprobe --pid 5088                        # 拓扑
hidprobe --pid 5088 --vgn-ws-battery       # 电量（3 次）
hidprobe --pid 5088 --vgn-ws-version       # 版本
hidprobe --pid 5088 --vgn-ws-settings      # 14 个设置项全解码（推荐先跑这个）
hidprobe --pid 5088 --vgn-ws-set light.brightness=3      # 写一项 → 回读 → 打印连带改动
hidprobe --pid 5088 --vgn-ws-set power.dormancy=180      # 改休眠（单位：秒）
hidprobe --pid 5088 --vgn-ws-set light.color=0xFF0000    # 改静态色（0xRRGGBB）
hidprobe --pid 5088 --vgn-ws-lock on       # 开 Win 锁（等价 --vgn-ws-set winlock=1）
hidprobe --pid 5088 --vgn-raw "04 00 00 1A 06 00 00 00"  # 注入任意帧

# 走完整驱动链路（Registry 竞标 → 会话 → IDeviceSettings → 协议）
src/Host.Wpf/bin/Debug/net10.0-windows/GearTray.exe --snapshot
src/Host.Wpf/bin/Debug/net10.0-windows/GearTray.exe --set light.brightness=3
```
