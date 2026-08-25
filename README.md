# HDMI Switch

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)](https://www.microsoft.com/windows)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Windows WPF 小工具：即時監看螢幕與本機 HDMI 輸出，並用 DDC/CI **一鍵把螢幕輸入切到 HDMI**。

適合桌機接 DisplayPort、偶爾要切去 HDMI（另一台電腦、筆電、遊戲機、擷取盒）的人。雙擊執行，不常駐服務、不改系統設定。

[English](#english)

---

## 這解決什麼問題

多輸入螢幕通常要走進 OSD 選單才能改輸入源。這支程式對每台支援 DDC/CI 的螢幕寫入 VCP `0x60`（Input Select），先試 HDMI-1（`0x11`），失敗再試 HDMI-2（`0x12`）。

同時用 Windows CCD API 列出本機顯示輸出，告訴你 **這台電腦的 HDMI 孔現在有沒有接上螢幕**（HPD / EDID）。

## 功能

- 即時監控（約 2 秒刷新；插拔螢幕會立刻更新）
- 每台使用中的螢幕：名稱、連接方式、目前輸入、DDC/CI 是否可用
- 本機 HDMI 孔有無螢幕：有訊號 / 無訊號
- **全部切到 HDMI**，或單台切換
- 若 HDMI 已接上螢幕但 Windows 還沒用，可「讓 Windows 使用此 HDMI」（`DisplaySwitch /extend`）
- 螢幕能力字串會用來列出可用輸入（HDMI-1 / DP-1 等）

## 訊號偵測：能做 / 不能做

| 想知道的事 | 做得到嗎 | 依據 |
|---|---|---|
| 這台電腦的 HDMI／DP 孔有沒有接上螢幕 | 可以 | GPU 熱插拔（HPD）與 EDID |
| Windows 現在有沒有對那台螢幕輸出畫面 | 可以 | 作用中的顯示路徑 |
| 螢幕**目前正在顯示**的輸入是哪一個 | 多半可以 | DDC/CI VCP `0x60` |
| 螢幕上「沒在用的那個 HDMI 孔」有沒有別台裝置的畫面 | **大多不行** | DDC 只能問正在顯示的那一孔 |

畫面上的綠燈「有訊號」= **本機該輸出有接到螢幕**，不是「另一台電腦正在送 HDMI」。

切到 HDMI 前，請確認目標孔真的有訊號來源。否則螢幕可能黑屏；若這台電腦不再是作用中的輸入，也會暫時失去 DDC 控制，要從 OSD 切回來。

## 需求

- Windows 10 或 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（自己建置則需要 SDK）
- 螢幕 OSD 開啟 **DDC/CI**（多數出廠已開）
- 用 HDMI／DP／DVI 連接；部分 USB 轉接或虛擬螢幕沒有 DDC

不需要系統管理員權限。

## 使用方式

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\HdmiSwitch.exe
```

直接執行 `HdmiSwitch.exe` 即可。視窗會列出螢幕與 HDMI 孔狀態：

1. 確認要切過去的 HDMI 有訊號來源
2. 按 **全部切到 HDMI**，或某一台的 **切到 HDMI**
3. 下方紀錄會寫每台成功或失敗原因

偵錯（不開視窗，把目前狀態寫到執行目錄的 `probe-result.txt`）：

```powershell
.\HdmiSwitch.exe --probe
```

## 常見問題

**按了切換沒反應**  
到螢幕 OSD 開啟 DDC/CI。內建筆電面板通常不能切輸入。USB 顯示轉接器也常不支援。

**切過去是黑的**  
目標 HDMI 當下沒有訊號。從 OSD 切回 DisplayPort／內建，或把有畫面的裝置接到該 HDMI。

**本機顯示 4 個 HDMI 孔都無訊號**  
代表顯示卡／主機板上的 HDMI 目前沒接螢幕。這不影響「把已用 DP 接上的螢幕切到它自己的 HDMI 孔」——那是螢幕輸入切換，不是切這台電腦的 GPU 輸出。

**兩台螢幕只有一台切成功**  
每台的 DDC 支援與 HDMI 編號不同。先看該台卡片上的可用輸入；必要時用 OSD 對一下 HDMI-1 / HDMI-2。

## 技術摘要

| 層 | 用途 | Windows API |
|---|---|---|
| CCD | 列出輸出孔、連接器類型、是否有螢幕 | `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` |
| DDC/CI | 讀寫目前輸入源 | `dxva2.dll`：`GetVCPFeature` / `SetVCPFeature` |
| 能力字串 | 解析 `vcp(60(11 12 0F))` 這類 HDMI／DP 代碼 | `CapabilitiesRequestAndCapabilitiesReply` |
| 桌面切換 | 啟用尚未使用的外接輸出 | `DisplaySwitch.exe /extend` |

輸入代碼依 MCCS：`0x11` HDMI-1、`0x12` HDMI-2、`0x13` HDMI-3、`0x0F` DisplayPort-1。

程式碼在 `Services/`（`CcdService`、`DdcService`、`MonitorHub`）與 `Native/`。

## 開發

```powershell
git clone git@github.com:kay5124/hdmi-switch.git
cd hdmi-switch
dotnet run
```

歡迎 issue 與 PR：其他輸入源、多 HDMI 編號設定、系統匣常駐都可以討論。

## 授權

[MIT](LICENSE)。歡迎使用、修改、再發布。

---

## English

A small Windows WPF utility that monitors connected displays and switches monitor input to HDMI over **DDC/CI**.

It is meant for desktops that stay on DisplayPort and occasionally need the monitor on HDMI (another PC, laptop, console, or capture device). Double-click to run; no background service.

### What it can and cannot detect

- **Yes:** whether *this PC’s* HDMI/DP port has a sink (HPD/EDID), whether Windows is currently driving that display, and (usually) which input the monitor is showing now.
- **No (on most monitors):** whether an *unused* HDMI port on the monitor has signal from another device. DDC only talks on the currently active input.

The green “has signal” badge means this PC sees a display on that output. It does not mean another machine is sending HDMI.

### Build

Windows 10/11, .NET 8, DDC/CI enabled in the monitor OSD.

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\HdmiSwitch.exe
```

Switching tries HDMI-1 (`0x11`) then HDMI-2 (`0x12`). If the destination HDMI has no source, the screen may go black and this PC will lose DDC until you switch back in the OSD.

MIT licensed. Issues and PRs welcome.
