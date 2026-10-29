# 实测：MCHOSE A7 V2 Ultra+ 私有协议（M3 已完成）

> 采集时间：2026-10-29
> 采集方式：`hidprobe --pid 100B --mchose-battery` / `--caps` / `--raw-feature`
> 采集机器：本机（Windows，.NET SDK 10.0.400）
> 状态：**已跑通并接入驱动**，`Drivers.McHose` 读电量 97%，三次一致。

---

## 1. 结论

| 项 | 结论 |
|---|---|
| 通信方式 | **Feature Report**（不是 Output Report） |
| 控制接口 | Usage Page `0xFF01`，`FeatureReportByteLength = 65` |
| 控制报表 | Report ID `0x11`，载荷 20 字节 → 缓冲区含 ID 共 **21** 字节 |
| 配置报表 | Report ID `0x12`，载荷 64 字节 → 缓冲区 65 字节 |
| 载荷编码 | **整体 XOR 0xFF**，未用字节补 `0xFF`（等价于补零后取反） |
| 电量命令 | 发 `0x11 [0x06]`，`data[8]`（取反数组 `[10]`）= 电量，`data[9]`（`[11]`）= 充电中 |
| 读一次够吗 | **不够**。dongle 要 1~3 个 RF 周期取回无线端数据，首次常回全零，必须重试 |

对应的实现：`src/Drivers.McHose/McHoseProtocol.cs`。

---

## 2. 接口拓扑的正确解读

`--caps` 打印的报表描述符是决定性证据：

```
VID_3837&PID_100B page=0xFF0B
  input   page=0xFF0B id=0x2A bits=8 count=60  usage=0x0060..0x0060
  output  page=0xFF0B id=0x2A bits=8 count=60  usage=0x0061..0x0061
  feature page=0xFF0B id=0x2A bits=8 count=60  usage=0x0062..0x0062
VID_3837&PID_100B page=0xFF01
  feature page=0xFF01 id=0x11 bits=8 count=20  usage=0x0022..0x0022
```

两个厂商页的分工：

| 接口 | 形状 | 实际用途 | 证据 |
|---|---|---|---|
| `0xFF01` | feature `0x11`/20B | **控制通道**（可下发命令） | 0x11 电量查询成功；SetFeature 接受 |
| `0xFF0B` | in/out/feature `0x2A`/60B | 只读状态/透传 | `HidD_SetFeature` 一律返回 `ERROR_INVALID_FUNCTION(1)` |

之前把 `0xFF0B` 当成"主控制接口"是错的——它有 OUT 报表、能 `CreateFile(GENERIC_WRITE)`，
但**写不进去**。`0xFF0B` 的 feature 0x2A 能读，返回的数据里第 9 字节也是电量（98），
可作为交叉校验，但不能作为命令通道。

---

## 3. 电量查询的完整字节

请求（21 字节，`Report ID` 在 `[0]`，不参与反转）：

```
11 F9 FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF
   ^^ 0x06 ^ 0xFF
```

应答（实测）：

```
11 F9 C8 C7 DE BF FA D1 FD FB F6 9E FF 9E FF FF FF FF FF FF FF
^^ Report ID
   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ 载荷，未解码
```

去掉 ID 后整体取反：

```
06 37 38 21 40 05 2E 02 04 09 61 00 61 00 00 00 00 00 00 00
^  ^                          ^  ^
0  命令回显 0x06             [10] [11]
                             97%   0 = 未充电
```

- `[10]` 电量：`0x61` = 97。老固件用 `1..10` 表示 `10%..100%`（需要 ×10），新固件直接给百分比。
- `[11]` 充电：`1` = 充电中，`0` = 放电。

> 索引口径的坑：公开文档写的是 `data[8]` / `data[9]`，但那个 `data` 是**跳过前 2 字节之后**的切片。
> 换算到"取反后的完整载荷"就是 `[10]` / `[11]`。差一位就会把 97% 读成 0x09=9%。

---

## 4. 无效应答长什么样

dongle 还没从鼠标取回数据时，应答是：

```
11 F9 C8 ... → 去 ID → FF FF FF ...  → 取反 → 06 00 00 00 ... 00
```

即「命令回显 + 全零」。**不能把这种应答当 0% 电量**——0% 的鼠标早关机了。
判据：电量 `0` 或 `>100` 一律视为无效，进入下一次重试。

实测中 5 次重试里前 2 次常常是空应答，第 3 次才拿到真值，所以重试次数不能少于 3。

---

## 5. 踩到的两个真坑

### 5.1 阻塞读 + 并发写 = 进程被静默杀死

在 `0xFF0B` 上用**同步** `ReadFile` 挂一个监听线程，再向同一路径的另一个句柄 `WriteFile`，
进程直接消失：无托管异常、无 stderr，退出码 `0xC0000374`。

解法：读通道必须 `FILE_FLAG_OVERLAPPED` + 事件等待 + `CancelIoEx`（`HidInputChannel`）。
同步阻塞的 IRP 会占住驱动同步路径。

### 5.2 `HidP_GetValueCaps` 导致堆破坏

`HIDP_VALUE_CAPS` 结构体按 SDK 定义声明为 60 字节（`Marshal.SizeOf` 也是 60），
但调用后进程随机崩在**别处**，退出码 `0xC0000374`（堆破坏）。同样的命令有时成功有时段错误。

处置：探测阶段可以读（一次拿到 `0x2A` / `0x11` 的报表布局，价值很高），
但**运行时不依赖它**。驱动直接写死已知的 Report ID 与长度。
如果后续要恢复动态发现，需要改用 `HidP_GetButtonCaps` 或重新核对结构体对齐，别在托管数组上赌。

---

## 6. 复现命令

```bash
dotnet build GearTray.slnx

# 描述符布局（Report ID 的权威来源）
hidprobe --pid 100B --caps

# 读电量（驱动级，含重试与解码）
hidprobe --pid 100B --mchose-battery

# 完整管线：枚举 → 驱动竞标 → 按物理设备去重 → 轮询
hidprobe --poll
```

`--poll` 实测输出：

```
devices: 3
MCHOSE A7 V2 Ultra+            97%      Online       0xFF01  VID_3837&PID_100B
MCHOSE V9 Turbo+               —        Online       0x000C  VID_3837&PID_600A
VGN N75 PRO 2.4G Dongle        —        Online       0x0001  VID_320F&PID_5088
battery resolved: 1/3
```
