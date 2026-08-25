# HDMI 監控切換

Windows WPF 小工具：即時監看目前螢幕與本機 HDMI 輸出，並一鍵把支援 DDC/CI 的螢幕輸入切到 HDMI。

## 能做什麼

- 列出目前正在使用的螢幕、連接方式（HDMI / DisplayPort / 內建）與目前輸入源
- 偵測**這台電腦的 HDMI 孔**有沒有接上螢幕（HPD / EDID）
- 一鍵或單台把螢幕輸入切到 HDMI（先試 HDMI-1，失敗再試 HDMI-2）
- 若 HDMI 已接上螢幕但 Windows 尚未使用，可延伸桌面到該輸出

## 做不到什麼

螢幕上「目前沒在用的那個輸入孔」有沒有別台裝置的畫面，多數螢幕無法透過 DDC/CI 從這台電腦判斷。綠燈「有訊號」指的是本機該輸出有接到螢幕，不是另一台電腦正在送 HDMI。

切換輸入前請確認目標 HDMI 真的有訊號來源；否則螢幕可能黑屏，而且這台電腦會暫時失去 DDC 控制。

## 需求

- Windows 10 / 11
- [.NET 8 桌面執行環境](https://dotnet.microsoft.com/download/dotnet/8.0)
- 螢幕需在 OSD 開啟 **DDC/CI**（多數預設已開）

## 使用

Release 建置：

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\HdmiSwitch.exe
```

或直接執行已建置的 `HdmiSwitch.exe`。

## 專案

| 項目 | 說明 |
|---|---|
| 帳號 | [kay5124](https://github.com/kay5124) |
| 授權 | 僅供個人使用，未另附授權條款 |
