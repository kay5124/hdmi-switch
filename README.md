# HDMI Switch

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D4)](https://www.microsoft.com/windows)
[![Release](https://img.shields.io/github/v/release/kay5124/hdmi-switch)](https://github.com/kay5124/hdmi-switch/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Windows 桌面程式。以 [DDC/CI](https://en.wikipedia.org/wiki/Display_Data_Channel) 切換螢幕輸入來源（HDMI／DisplayPort／VGA／DVI），並依實際桌面配置識別每台螢幕的位置。

![主畫面](docs/screenshot.png)

[English](#english)

## 功能

- 依 Windows 桌面座標排列螢幕，標示左／中／右（或上／下）
- 標示這個視窗與滑鼠目前所在的螢幕
- 在實體螢幕上顯示編號（與系統「識別顯示器」相同用途）
- 讀取目前輸入來源；點名稱切單台，或一次把多台切到同一類輸入
- 以本機實際接線標示哪個輸入可能有畫面，降低切到沒訊號而黑屏的機會
- 偵測本機 HDMI 輸出孔是否接上螢幕（HPD / EDID）
- 可將尚未使用、但已接上螢幕的 HDMI 輸出納入 Windows 桌面
- 單實例執行；啟動時先讀取顯示狀態再進入主畫面

不常駐背景服務，也不會修改系統顯示設定（延伸桌面除外，需使用者自行按下）。

## 安裝

從 [Releases](https://github.com/kay5124/hdmi-switch/releases/latest) 下載 `HdmiSwitch-*-win-x64.zip`，解壓後執行 `HdmiSwitch.exe`。

套件為 self-contained，無需另外安裝 .NET。僅支援 Windows 10 / 11 x64。

## 系統需求

- Windows 10 或 11（x64）
- 螢幕 OSD 啟用 DDC/CI（多數機種預設開啟）
- HDMI、DisplayPort 或 DVI 連接。部分 USB 顯示轉接器與虛擬螢幕不提供 DDC/CI
- 不需要系統管理員權限

從原始碼建置時需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

## 使用

1. 以畫面上的編號與左／中／右對應實體螢幕；不確定時按「識別」或「識別全部」。
2. 綠色輸入是這台電腦有在輸出的來源；紅色表示本機沒接到，點下去會先警告。
3. 要切單台就點該螢幕上的輸入名稱；要一次切多台就用下方「全部切到」。

切換後若畫面全黑，請用螢幕 OSD 切回原先輸入。當這台電腦不再是作用中的輸入時，DDC/CI 會暫時無法控制該螢幕。

## 偵測範圍

| 項目 | 支援 | 說明 |
| --- | --- | --- |
| 本機 HDMI／DisplayPort 孔是否接上螢幕 | 是 | GPU 熱插拔與 EDID |
| Windows 是否正在對該螢幕輸出 | 是 | 作用中的顯示路徑 |
| 螢幕目前顯示的輸入來源 | 通常可以 | DDC/CI VCP `0x60` |
| 螢幕上閒置輸入孔是否有其他裝置畫面 | 通常不行 | DDC/CI 只能詢問目前作用中的輸入 |

介面上的「有訊號」表示**這台電腦的該輸出有接到螢幕**，不表示另一台裝置正在傳送 HDMI。

## 建置

```powershell
git clone https://github.com/kay5124/hdmi-switch.git
cd hdmi-switch
dotnet build -c Release
.\bin\Release\net8.0-windows\HdmiSwitch.exe
```

## 疑難排解

| 狀況 | 可能原因 |
| --- | --- |
| 按下切換沒有反應 | OSD 未開啟 DDC/CI；筆電內建面板或 USB 轉接器通常不可切換輸入 |
| 切換後黑屏 | 目標 HDMI 當下沒有訊號 |
| 本機 HDMI 孔皆顯示無訊號 | 顯示卡／主機板的 HDMI 未接螢幕。仍可把已用 DisplayPort 連接的螢幕切到該螢幕自己的 HDMI 孔 |
| 僅部分螢幕切換成功 | 各螢幕的 DDC/CI 與 HDMI 編號不一定相同 |

## 實作摘要

| 層 | 用途 | API |
| --- | --- | --- |
| CCD | 輸出孔、連接器類型、是否有螢幕 | `QueryDisplayConfig`、`DisplayConfigGetDeviceInfo` |
| 桌面配置 | 螢幕座標與目前視窗／游標所在 | `GetMonitorInfo`、`MonitorFromWindow` |
| DDC/CI | 讀寫輸入來源 | `dxva2.dll`：`GetVCPFeatureAndVCPFeatureReply`、`SetVCPFeature` |
| 能力字串 | 解析螢幕支援的輸入代碼 | `CapabilitiesRequestAndCapabilitiesReply` |

輸入代碼依 VESA MCCS：HDMI-1 `0x11`、HDMI-2 `0x12`、HDMI-3 `0x13`、DisplayPort-1 `0x0F`。

## 授權

本專案以 [MIT License](LICENSE) 授權。

---

## English

Windows desktop app that switches monitor input (HDMI / DisplayPort / VGA / DVI) over DDC/CI and maps each display to its real desktop position (left / center / right).

![Main window](docs/screenshot.png)

### Install

Download `HdmiSwitch-*-win-x64.zip` from [Releases](https://github.com/kay5124/hdmi-switch/releases/latest) and run `HdmiSwitch.exe`. Self-contained; Windows 10/11 x64. No separate .NET runtime required.

### Capabilities

| Question | Supported |
| --- | --- |
| Does this PC’s HDMI/DP port have a display attached? | Yes (HPD / EDID) |
| Is Windows currently driving that display? | Yes |
| Which input is the monitor showing now? | Usually (VCP `0x60`) |
| Does an unused HDMI port on the monitor have signal from another device? | Usually no |

“Has signal” means this PC sees a sink on that output. It does not mean another machine is sending HDMI.

Click an input name to switch one monitor, or use the family buttons to switch all. Inputs this PC is not driving are marked and confirmed before switching. If the destination has no source, the screen may go black; restore the previous input from the monitor OSD.

### Build

```powershell
git clone https://github.com/kay5124/hdmi-switch.git
cd hdmi-switch
dotnet build -c Release
```

MIT licensed. Issues and pull requests are welcome.
