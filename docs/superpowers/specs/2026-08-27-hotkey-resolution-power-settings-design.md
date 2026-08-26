# HDMI Switch：Hotkey / 解析度 / 電源管理 / 輸入自訂命名 設計文件

- 日期：2026-08-27
- 狀態：待實作
- 相關檔案：`MainWindow.xaml(.cs)`、`Services/*`、`Native/NativeMethods.cs`

## 背景與目標

目前這個 app 是一個純即時讀取的 HDMI/DP/VGA/DVI 切換器，沒有任何設定持久化機制。使用者（會在 Mac 和 PC
之間切換同一台螢幕）想要：

1. 用全域快捷鍵一鍵把螢幕切回 PC（或切去 Mac），不用滑鼠點卡片。
2. 螢幕上如果有 Type-C（USB-C／DP Alt Mode）輸入，UI 要能明確標示，跟一般 DisplayPort 區分開。
3. 切換螢幕解析度。
4. 關閉螢幕（單台或全部），並且能定時自動關（倒數或每天固定時間），也能手動點了就關。

這份文件涵蓋以上四塊功能的架構設計；不含逐行程式碼，實作細節留給 writing-plans 產出的計畫。

## 範圍

**這次要做：**
- 設定持久化基礎（JSON 存檔）
- Hotkey 情境切換（依 `InputFamily` 廣播，重用現有批次切換邏輯）
- 輸入名稱自訂（含「本機 Type-C 連接」自動標示 + 手動輸入標籤覆寫）
- 關閉螢幕：單台（DDC）+ 全部（Windows 系統指令）
- 定時電源：每天固定時間（持久化）+ 倒數計時（一次性，不持久化）+ 立即關閉按鈕
- 解析度切換（純 UI 按鈕/下拉，不綁 hotkey）

**這次不做（列在「不在範圍內」章節）：**
- 開機時螢幕自己跳出的訊號 OSD（螢幕韌體行為，另開小調查）
- 自動測試專案（現有專案本身沒有測試專案，YAGNI）

## 架構總覽

沿用現有的「靜態 Service 類別 + WPF code-behind」風格，不引入 DI 容器。新增的狀態（設定檔、hotkey
註冊、排程計時器）全部由 `MainWindow` 持有生命週期並注入到新 Service，維持現有 `MonitorHub` /
`DdcService` / `CcdService` 全靜態、無狀態查詢的設計不變。

```
MainWindow
 ├─ AppSettings (載入/存檔，POCO)
 ├─ HotkeyManager (包 RegisterHotKey，事件驅動)
 ├─ PowerScheduler (DispatcherTimer 驅動每日排程 + 倒數)
 ├─ ResolutionService (查詢/套用解析度，靜態，同 MonitorHub 風格)
 └─ SettingsWindow (設定 Hotkey / 定時關閉 / 輸入命名的 UI)
```

## 詳細設計

### 1. 設定持久化（`Services/AppSettings.cs`）

新增一個 POCO + 讀寫方法，存在 `%AppData%\HdmiSwitch\settings.json`（`System.Text.Json`）。開機讀一次
（讀不到或格式壞掉就視同空設定，不丟例外中斷啟動），任何一塊設定變更就整份覆寫存檔。

```csharp
public sealed class AppSettings
{
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
    public List<DailyPowerSchedule> DailySchedules { get; set; } = [];
    public List<InputLabelOverride> InputLabelOverrides { get; set; } = [];
}

public sealed class HotkeyBinding
{
    public InputFamily Family { get; set; }
    public uint Modifiers { get; set; }   // MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN 的 OR 組合
    public uint Key { get; set; }         // Virtual-Key Code
}

public sealed class DailyPowerSchedule
{
    public Guid Id { get; set; }
    public string? TargetGdiName { get; set; }  // null = 全部螢幕
    public TimeSpan Time { get; set; }           // 當地時間 HH:mm
    public bool Enabled { get; set; }
}

public sealed class InputLabelOverride
{
    public string MonitorKey { get; set; } = "";  // 見 3.2 的 key 定義
    public byte InputCode { get; set; }
    public string Label { get; set; } = "";
}
```

倒數關閉（countdown）刻意**不**放進 `AppSettings`——它是「現在啟動一個一次性計時」的運行期狀態，
app 關掉重開就該消失，持久化反而會製造「上次忘記取消的倒數，重開後又跳出來」的困惑。

### 2. Hotkey 情境切換

**情境＝現有的 `InputFamily`**（HDMI / DisplayPort / VGA / DVI），不另外設計「情境」這個新概念。理由：
使用者的實際場景——PC 走 HDMI、MacBook 走 Type-C（GPU 端技術類型是 `DisplayPortUsbTunnel`，已經被
`InputSelect.FamilyFromTechnology` 歸進 `InputFamily.DisplayPort`）——剛好就是兩個不同 family，跟現有
「全部切到 X」批次功能（[MainWindow.xaml.cs:325](../../../MainWindow.xaml.cs)）完全對得上，包含它既有的
「只切偵測到有訊號的螢幕、其餘略過並 Log」邏輯。

**做法：**
- 把 `SwitchAllFamily_OnClick` 裡的核心邏輯抽成可重用的 `SwitchAllFamilyAsync(InputFamily family)`，
  按鈕點擊跟 hotkey 觸發都呼叫這個方法，行為完全一致（含跳過無訊號螢幕的 Log）。
- 新增 `Services/HotkeyManager.cs`：包裝 `RegisterHotKey`/`UnregisterHotKey`，對每個 `HotkeyBinding`
  註冊一個唯一 id，`MainWindow` 既有的 `WndProc`（[MainWindow.xaml.cs:119](../../../MainWindow.xaml.cs)）
  多處理一個 `WM_HOTKEY` 訊息，轉發給 `HotkeyManager` 找出對應 family 後呼叫
  `SwitchAllFamilyAsync`。
- **註冊失敗要顯性處理**：`RegisterHotKey` 常見失敗原因是組合鍵被系統或別的程式佔用。失敗時不能靜默
  吞掉——`HotkeyManager` 回傳每個 binding 的成功/失敗清單，`SettingsWindow` 存檔時把失敗的組合鍵標紅
  並顯示原因，同時寫一筆 Log。
- 快捷鍵設定 UI：`SettingsWindow` 列出目前偵測到的 `InputFamily`（沿用 `BatchInputOptions` 邏輯），
  每個一個「按鍵擷取欄」——聚焦後攔截下一次 `PreviewKeyDown` 組合，顯示成「Ctrl+Alt+P」，可清除。

### 3. 輸入名稱自訂（含 Type-C 標示）

分兩層，因為技術上能保證的程度不一樣：

**3.1 本機自己的連接技術（可靠、自動）**

`CcdService.ConnectorName`（[CcdService.cs:81](../../../Services/CcdService.cs)）目前把
`OutputTechnology.DisplayPortUsbTunnel` 顯示成「USB DisplayPort」——這個判斷本來就是 Windows API
保證準確的（這台 PC 自己是不是用 USB-C/DP Alt Mode 輸出），純粹是字串不夠白話。直接改成「Type-C」。

**3.2 螢幕上其他輸入孔（不可靠、需要手動覆寫）**

DDC/CI 的輸入代碼（VCP 0x60 回報的 0x0F/0x10 等）是螢幕韌體自訂的，沒有標準代碼代表「這是 Type-C
孔」，而且 Mac 那端不會跑這個 app，Windows 也看不到 Mac 的連接方式。純軟體無法保證自動判斷哪個代碼
對應螢幕上的哪個實體孔。

做法：既然本來就要做設定持久化，順便加「輸入名稱覆寫」——使用者在 `SettingsWindow` 對某台螢幕的某個
輸入代碼手動改名（例如把偵測到的「DisplayPort-1」改叫「Type-C」）。

- **Key 設計**：`InputLabelOverride.MonitorKey` 用 `GpuOutput.MonitorName`（EDID 友善名稱，例如
  "DELL U2723QE"）；抓不到時退回 DDC `Description`。**已知限制**：如果同型號螢幕買兩台，覆寫會套用到
  兩台身上（無法用型號名稱區分兩台一模一樣的螢幕）。這是刻意的簡化，不做 EDID 序號解析（YAGNI）；
  如果使用者實際有這個情境要再另外處理。
- **套用時機**：`MonitorHub.Capture` 保持現有的純查詢、無狀態設計不變；覆寫改成在
  `MainWindow.ApplySnapshot`（[MainWindow.xaml.cs:177](../../../MainWindow.xaml.cs)）合併快照後，對
  `Inputs` 清單逐一比對 `(MonitorName, InputCode)`，命中就用 `chip with { Label = override.Label }`
  換掉顯示名稱（`InputChip` 是 record，`with` 表達式免可變狀態）。

### 4. 關閉螢幕

**單台**（`Services/DdcService.cs` 新增 `PowerOff`）：用 DDC VCP `0xD6`（Power Mode），送值 `0x04`
（軟關機）。跟現有 `SwitchInput` 同一套目標查找/開啟 physical monitor 的邏輯，成功/失敗訊息走一樣的
`SwitchResult` 格式。不支援 DDC 的螢幕（`CanSwitch == false`）沿用現有 UI 灰階邏輯，不特別重做。

> 假設：選軟關機（0x04）而非硬關機（0x05）——軟關機通常靠訊號恢復或螢幕實體電源鍵喚醒，行為上跟一般
> 螢幕自動休眠接近。如果實測某台螢幕軟關機後叫不醒，再評估要不要加開關選硬關機。

**全部**（`Services/MonitorHub.cs` 新增 `PowerOffAllWindows`）：用 `PostMessage(HWND_BROADCAST,
WM_SYSCOMMAND, SC_MONITORPOWER, 2)`，不靠 DDC，對所有螢幕都有效但沒辦法只關一台。**注意這也會關掉這個
app 視窗所在的螢幕**——這是預期行為，觸發時 Log 要講清楚「已送出全部螢幕關閉指令，移動滑鼠或按鍵盤即可
喚醒」，避免使用者以為當機。

**UI**：每張卡片加一顆「關閉」按鈕（跟現有「識別」並排，同樣風格）觸發單台關閉；底部批次列
（現有「識別全部」/「全部切到」那排）加一顆「全部關閉」。兩者都是立即動作，滿足「點了就關閉」。

### 5. 定時電源排程（`Services/PowerScheduler.cs`）

一個物件處理兩種模式，行為分開：

- **每天固定時間**（持久化，`DailyPowerSchedule` 清單）：用一顆 `DispatcherTimer`（Interval 60 秒）
  tick 時比對現在時間的 `HH:mm` 是否命中任一筆 `Enabled` 規則；用 `Dictionary<Guid, DateOnly>` 記錄
  「這筆規則今天觸發過了沒」防止同一分鐘內因為 tick 時間誤差重複觸發兩次。命中就呼叫跟按鈕一致的
  `PowerOff`/`PowerOffAllWindows`，並 Log。
- **倒數計時**（不持久化，運行期狀態）：使用者在 UI 輸入目標（全部或某台）+ 分鐘數，按下開始後記錄
  `DateTime` 到期時間，同一顆 60 秒 tick 的 timer 一併檢查是否到期；到期後跟每天排程一樣呼叫關閉，並
  從運行期清單移除（不寫回設定檔）。UI 上顯示「倒數中：剩餘 N 分鐘」+ 一顆「取消倒數」。

**UI 位置**：每天固定排程的新增/刪除/開關清單放在 `SettingsWindow`（跟 Hotkey 設定同一個視窗，不占
主畫面版面）；倒數計時是常用的即時操作，放在主視窗底部批次列，跟「全部關閉」放一起（目標下拉 + 分鐘
輸入 + 開始/取消按鈕）。

### 6. 解析度切換（`Services/ResolutionService.cs`）

新增 P/Invoke：`EnumDisplaySettingsEx`、`ChangeDisplaySettingsEx`、`DEVMODE` struct
（`Native/NativeMethods.cs`，沿用現有「所有 Win32 簽章集中在這支檔案」的慣例）。

- `ListResolutions(gdiDeviceName)`：列舉該 GDI 裝置支援的所有模式，用 `寬×高` 去重（同解析度多個更新
  頻率只留最高的一個，避免下拉選單塞爆），由大到小排序。
- `Apply(gdiDeviceName, width, height, frequency)`：呼叫 `ChangeDisplaySettingsEx`，回傳值非
  `DISP_CHANGE_SUCCESSFUL` 時視為失敗（Windows 可能拒絕不支援的模式），走 Log，不拋例外中斷 UI。

**UI**：每張卡片加一個解析度 `ComboBox`，選項來自 `ListResolutions`，預選值對應
`OutputItem.PixelWidth/PixelHeight`；`SelectionChanged` 直接套用（不用 hotkey，使用者已選這個
方案）。

## 受影響檔案清單

**新增：**
- `Services/AppSettings.cs`
- `Services/HotkeyManager.cs`
- `Services/PowerScheduler.cs`
- `Services/ResolutionService.cs`
- `SettingsWindow.xaml` / `SettingsWindow.xaml.cs`

**修改：**
- `Native/NativeMethods.cs` — 新增 hotkey / 電源 / 解析度相關 P/Invoke 與常數
- `Services/DdcService.cs` — 新增 `PowerOff`
- `Services/MonitorHub.cs` — 新增 `PowerOffAllWindows`，`Capture` 後段合併輸入標籤覆寫的掛勾點（實際
  覆寫邏輯放 `MainWindow`，`MonitorHub` 維持無狀態）
- `Services/CcdService.cs` — `ConnectorName` 的 Type-C 字串
- `MainWindow.xaml` / `MainWindow.xaml.cs` — 卡片新增關閉按鈕、解析度下拉；底部新增全部關閉/倒數/
  開啟設定視窗的按鈕；`WndProc` 處理 `WM_HOTKEY`；`ApplySnapshot` 套用輸入標籤覆寫

## 錯誤處理原則

延續現有風格：**失敗走 Log，不拋例外中斷 UI**。
- Hotkey 註冊失敗（組合鍵衝突）：`SettingsWindow` 內顯性標示 + Log，不靜默。
- DDC 關閉/解析度切換失敗：沿用 `SwitchResult`/Log 模式，訊息要講清楚「哪台、為什麼失敗」。
- 設定檔讀取失敗（檔案損毀/格式錯誤）：視同空設定，不阻擋啟動，Log 一筆警告。

## 驗證方式

現有專案沒有測試專案，這次也不新增（YAGNI，跟專案現況一致）。改用手動操作 checklist 驗證：
- Hotkey：設定→按下組合鍵→螢幕確實切換；組合鍵衝突→確認有錯誤提示。
- 關閉：單台/全部、按鈕/每天排程/倒數，各自觸發一次確認生效。
- 解析度：切換後畫面確實改變、Windows 顯示設定同步更新。
- 輸入標籤覆寫：改名後重開 app，名稱要還在（驗證存檔有生效）。
- 回歸：既有的輸入切換、識別、批次切換功能不能被這次改動影響。

## 不在這次範圍內

- **開機時螢幕自己跳出的訊號 OSD**：螢幕韌體行為，不保證能控制。列為後續小調查——做解析度/電源這段
  時順便讀一次 capabilities 字串，看有沒有相關 VCP 碼（例如廠牌自訂碼），有再評估加開關；沒有就是
  「螢幕自己選單裡關」，不會是這次的交付項目。
- 自動化測試（現有專案沒有，維持一致）。
- EDID 序號解析（用來區分同型號多台螢幕）——見 3.2 的已知限制。

## 開放問題 / 假設（實作前應留意，非阻塞）

1. DDC 軟關機（0x04）是否所有螢幕都能正常喚醒，需實測；不行的話再加硬關機切換。
2. `InputLabelOverride` 用 `MonitorName` 當 key，同型號多台螢幕會共用覆寫（已知限制，非 bug）。
3. 「全部關閉」會連帶關掉 app 自己所在的螢幕，這是預期行為，非螢幕失聯。
