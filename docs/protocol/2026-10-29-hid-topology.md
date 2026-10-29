# 实测：本机 2.4G 设备 HID 接口拓扑

> 采集时间：2026-10-29 12:5x
> 采集方式：`hidprobe`（M0 探测工具），`hidprobe --pid 100B --feature`
> 采集机器：本机（Windows，SDK 10.0.400）
>
> 本文件只记录**实测数据**。带「推测」标记的是待验证假设，不是结论。

---

## 1. 结论先说

| 结论 | 状态 |
|---|---|
| MCHOSE A7 V2 Ultra+ 存在**两个**厂商自定义接口，且**两个都可写** | ✅ 实测确认 |
| 因此「DPI / Windows 键锁定可写」这一 M4 前提**成立** | ✅ 前提排除风险 |
| A7 的两个厂商接口 Feature Report（Report ID 0–3）**全部无响应** | ✅ 实测确认 |
| 协议走 Output Report 而非 Feature Report | ⚠️ 推测，待 M3 验证 |
| A7 与 VGN 键盘**不共用接收器**（各自独立 VID/PID） | ✅ 实测确认 |

最后一条很重要：设计文档里担心的「两台设备共用 dongle 导致命令串行化粒度问题」**不存在**，
按设备串行化即可。

---

## 2. MCHOSE A7 V2 Ultra+（鼠标）

`VID_3837 & PID_100B` —— 共 5 个 HID 接口。

| Usage Page | IN | OUT | FEAT | 可写 | 判断 |
|---|---|---|---|---|---|
| `0xFF0B` | 61 | 61 | 61 | ✅ | **主控制接口**（厂商页） |
| `0xFF01` | 65 | 65 | 65 | ✅ | **次控制接口**（厂商页） |
| `0x0001` | 8 | 0 | 0 | ✅(见注) | 鼠标顶层集合 |
| `0x0001` | 11 | 2 | 0 | ✅(见注) | 键盘集合（`\KBD` 路径） |
| `0x000C` | 3 | 0 | 0 | ✅(见注) | 消费者控制（媒体键） |

> 注：`0x0001` / `0x000C` 这几条也被 `ProbeWritable` 判为可写，但与「系统独占顶层集合不可写」
> 的常识冲突，怀疑是 `GENERIC_WRITE` 打开成功但后续 `WriteFile`/`HidD_SetFeature` 仍会拒绝。
> M3 要用真实写入验证，不能只看能否打开。

### 报文长度推断

`61 = 1 + 60`，`65 = 1 + 64`。两个接口的载荷长度不同（60 vs 64），
说明它们**不是同一套协议的两个副本**，很可能是两种不同固件族的指令集。

### Feature Report 实测

```
0xFF0B  (feat=61)  id 0 / 1 / 2 / 3 → 全部 HidD_GetFeature 返回失败
0xFF01  (feat=65)  id 0 / 1 / 2 / 3 → 全部失败
```

对比参考项目里的 ATK/VXE 鼠标：作者明确写了「usage page `0xFF05`，输入 64 字节 / 输出 64 字节，
**没有 Feature Report**」，并且请求报文首字节就是 `0x08`（Report ID）。

→ **强推测**：A7 也是 Output Report 协议，指令通过 `WriteFile` 发到 `0xFF0B` 或 `0xFF01`，
Report ID 类似 `0x08`，应答从 IN 端点读回。M3 第一件事应是扫 Report ID `0x01`–`0x20` 的
Output 写入 + 读回，而不是继续试 Feature。

---

## 3. VGN N75 PRO 2.4G Dongle（键盘）

`VID_320F & PID_5088` —— 共 7 个 HID 接口。

| Usage Page | IN | OUT | FEAT | 可写 | 判断 |
|---|---|---|---|---|---|
| `0x0001` | 9 | 2 | 65 | ✅ | 键盘集合，**带 Feature Report** |
| `0x0001` | 16 | 0 | 0 | ✅ | 键盘附加集合 |
| `0x0001` | 2 | 0 | 0 | ✅ | 系统控制 |
| `0x000C` | 3 | 0 | 0 | ✅ | 消费者控制 |
| `0xFF1C` | 64 | 64 | 0 | ✅ | **主控制接口**（厂商页） |
| `0x0001` | 33 | 33 | 0 | ✅ | 键盘集合 + 输出（灯效？） |
| `0x0001` | 8 | 0 | 0 | ✅ | 鼠标兼容集合 |

关键差异：VGN 的**键盘集合带 65 字节 Feature Report**，而厂商页 `0xFF1C` 反而没有 Feature。
这跟 A7 刚好相反 —— 说明两家用的芯片方案不同（VGN 大概率是常见国产方案，A7 更像 Nordic）。

---

## 4. 同机其他 MCHOSE 设备（同 VID，供驱动匹配参考）

| 设备 | VID:PID | 厂商接口 | 备注 |
|---|---|---|---|
| MCHOSE V9 Turbo+（耳机） | `3837:600A` | `0xFF21`(2/2/16)、`0xFF22`(0/0/64)、`0xFF90`(64/64)、`0xFF82`(64/64) | 与参考项目的 DARKRIM 耳机同属「dongle 上报」类 |
| MCHOSE MagDock（充电底座） | `3837:1012` | `0xFF00`(65/65) | 可能是充电状态来源 |
| V9 Turbo | `3837:6008` | `0xFF00`(65/65) | 与 MagDock 接口形状一致 |

**注意**：`3837` 是 MCHOSE 的 VID，下面挂了多种设备。
驱动匹配**不能只看 VID**，必须 VID + PID + UsagePage，否则 A7 的驱动会误接管耳机和底座。

---

## 5. 对设计文档的修正

> **2026-10-29 后续更新**：本节的第 3、4 条已被 `2026-10-29-mchose-protocol.md` 推翻。
> A7 走的是 **Feature Report `0x11`**，不是 Output Report；可写通道是 `0xFF01`，
> `0xFF0B` 写不进去（`ERROR_INVALID_FUNCTION`）。下面的原始记录保留，以便对照当时的推断。

1. **「两台设备是否共用接收器」风险（文档 15.4）** → 已排除，各自独立。
2. **「是否存在可写控制接口」风险（文档 15.1）** → 已排除，A7 有两个。
3. ~~`ProbeWritable` 的实现要收紧~~ → **已用真实 `WriteFile` 取代**，见 `WriteProbeResult`。
4. ~~A7 的协议大概率是 Output Report + Report ID ≥ 0x08~~ → **错**。见协议文档。

---

## 6. 复现命令

```bash
# 全量接口拓扑
dotnet run --project src/Tools.HidProbe -- 

# 只看鼠标
dotnet run --project src/Tools.HidProbe -- --pid 100B

# 转储 Feature Report
dotnet run --project src/Tools.HidProbe -- --pid 100B --feature

# 跑一遍完整数据管线（枚举 → 建会话 → 轮询）
dotnet run --project src/Tools.HidProbe -- --poll
```
