# MCHOSE 音频设备（V9 系列耳机）电量协议

> 实测日期：2026-10-29
> 设备：`MCHOSE V9 Turbo+`（VID_3837 / PID_600A）
> 状态：已跑通，驱动层读到 100%（放电中），三次一致

---

## 1. 为什么网页驱动里看不到耳机

不是故障，是产品线分工。MCHOSE 官方支持文档原文：

> The M HUB **Web Driver does not currently support audio products**, including the V9 Turbo+.
> **Audio devices only work on client version**.

支持的音频设备列表（仅客户端）：`S9 Pro / G9 Pro / V9 Series / G20 Series / K20 / X9 Series`。

所以耳机必须用 **M HUB 桌面客户端**（`C:\Program Files\MCHOSE HUB`），
而键鼠走的是网页驱动（`mchose.com.cn` → `#/detail?deviceName=...`）。两套是不同软件。

官方规格里写明该产品支持 `Battery Status Display / Real-time battery level monitoring`，
M HUB 客户端也确实做了「托盘显示已连接设备电量」。**电量是存在的，只是不在网页端。**

---

## 2. 客户端结构（逆向切入点）

Electron 应用，但主进程被 **bytenode 编译成 V8 字节码**（`out/main/index.jsc`，929 KB），
`grep` 不到任何 ASCII 字符串，反汇编代价过高。

真正的入口在日志里：

```
[main:index] renderer loaded from remote:
  mchose-renderer://app/cdn.mchose.com.cn/customPage/prod/index.html?v=1.1.26
```

**渲染层从远端 CDN 加载，是明文 JS**。直接下载即可，无需碰字节码：

```
https://cdn.mchose.com.cn/customPage/prod/index.html?v=1.1.26
  └ assets/main-xA7mzCYt.js                        (1.1 MB, 路由与设备表)
      └ assets/headsetPage-DNy5Z21r.js              (2.5 MB, 耳机页面)
          ├ assets/hidDeviceOptions-Blc1p4EV.js     ← 请求帧构造
          ├ assets/webHIDBase-DFlt-On6.js           ← sendReport 封装
          └ assets/crc32-BlQnRCeo.js                ← $AA 帧解析 + 电量解码
```

日志路径：`%APPDATA%\MCHOSEHUB\files\log\YYYYMMDD.txt`（含渲染层 URL 与版本号）。

---

## 3. HID 通道

```
接口 0xFF90   in=64  out=64  feature=0
Report ID 0x55（85）
```

同一只耳机挂了 6 个接口，只有这个是电量通道：

| 接口 | 种类 | Report ID | 用途 |
|---|---|---|---|
| 0xFF21 | in / out / feature | 0xED | 状态（feature 读回 `ED 02 02 0E …`） |
| 0xFF22 | feature | 0xAA | 自定义图片/配置 |
| 0x000C | input | — | 多媒体键 |
| 0x000B | input/output | — | 电话（HID Telephony） |
| **0xFF90** | **input + output** | **0x55** | **电量 / 状态（本文档）** |
| 0xFF82 | input + output | 0x41 | 另一条私有通道 |

> 注意：`--caps` 的 value caps 里 0xFF90 只列出 input usage，
> 但 `HidD_GetCaps` 报 `OutputReportByteLength=64`，实测 `WriteFile` 可用。
> **别只看 value caps 判断有没有 output 通道。**

---

## 4. 协议

### 4.1 请求（Output Report，64 字节）

```
55 65 01 00 00 ... 00
│  │  │
│  │  └─ [2] 参数：0x01 = 读取
│  └──── [1] 命令字：0x65（101）
└─────── [0] Report ID：0x55
```

### 4.2 应答（Input Report，64 字节）

```
55 65 64 02 00 ... 00
│  │  │  │
│  │  │  └─ [3] 状态码
│  │  └──── [2] 电量 0..100
│  └─────── [1] 命令回显 0x65
└────────── [0] Report ID 0x55
```

本机实测（V9 Turbo+，满电放电中）：`55 65 64 02` → `0x64` = 100%，`0x02` = 放电。

### 4.3 状态码

| 值 | 含义 |
|---|---|
| 2 | 放电中（discharge） |
| 3 | 充电中（charging） |
| 4 | 满电（full） |
| 26 (0x1A) | 设备睡眠，此时电量无意义 |

### 4.4 重试

官方实现重试 3 次、每次超时 1000 ms。首次常拿不到（dongle 需要向无线端取数），
**必须重试**，且每次发帧前先 `Drain()` 排掉残留帧，否则会把上一次的旧应答当成这次的。

---

## 5. 偏移陷阱（会把 100% 读成 101）

官方 JS 里写的是：

```js
r = i[1];   // 电量
l = i[2];   // 状态
```

但那是 **WebHID 的 `event.data`**，按 W3C 规范它 **不包含 Report ID**。
落到 Win32 `ReadFile` / 我们自己的 `HidInputChannel` 上，报文是含 ID 的：

```js
WebHID  i[0] = 命令字 0x65      →  raw[1]
        i[1] = 电量             →  raw[2]
        i[2] = 状态             →  raw[3]
```

照抄官方下标会把命令回显 `0x65`（=101）当成电量显示。
**这条与 MCHOSE 鼠标那套 `Skip(2)` 的偏移陷阱是同一类错误，已第二次踩到，统一记入红线。**

---

## 6. 实现落点

`src/Drivers.McHose/McHoseHeadsetDriver.cs`

- 匹配：`VID 0x3837 + PID 0x600A + UsagePage 0xFF90` → 100 分
- 同族未验证的音频接口（仅 0xFF90）→ 60 分
- **其它接口一律 0 分**：同一只耳机 6 个接口，全给分就退化成「一只耳机 6 张卡片」
- 会话持有两个独立句柄：`HidConnection`（写）+ `HidInputChannel`（读）
- 读失败保留上次值并标 `IsStale`，不清空界面

验证命令：

```bash
hidprobe --pid 600A --page 0xFF90 --raw "55 65 01" --timeout 2000
hidprobe --poll        # 应显示 V9 Turbo+ 的电量
```
