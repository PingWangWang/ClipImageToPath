# 项目记忆：ClipImageToPath

Windows 托盘常驻程序（.NET 8 WinForms，net8.0-windows，WinExe，单文件发布）。
核心功能：检测剪贴板图片 → 保存临时 PNG 到 %TEMP% → 回写路径文本（替代原图片）。

## 模块
- `Program.cs`：入口装配，创建 ClipboardMonitor / ImageToPathService，连线事件后运行 TrayApplicationContext。单实例 Mutex。
- `ClipboardMonitor.cs`：原生隐藏窗口，AddClipboardFormatListener 监听 WM_CLIPBOARDUPDATE。
- `ImageToPathService.cs`：转换核心（判图→落盘→延迟回写），含指纹去重与挂起回写取消。
- `TrayApplicationContext.cs`：NotifyIcon + 右键菜单（开机自启/消息提示/退出）+ 气泡提示。
- `Logger.cs`：文件日志到 %TEMP%/ClipImageToPath/clip.log。

## 约定（重要，新增代码须沿用）
- 开关类设置统一模式：ToolStripMenuItem(CheckOnClick=true) + 注册表。
  - 开机自启：Run 键 `Software\Microsoft\Windows\CurrentVersion\Run` 值 `ClipImageToPath`。
  - 消息提示：Settings 键 `Software\ClipImageToPath` 值 `ShowBalloonTip`（DWord，缺省 1）。
  - 功能开关（图片转路径）：同 Settings 键 值 `FeatureEnabled`（DWord，缺省 1，默认开启）。
- --selftest 模式不显示托盘，需强制功能开启以保证端到端自检可用。
- 版本号单一来源：csproj `<Version>`（当前 1.0.4），publish.ps1 读取。
- 关闭功能时须同时取消挂起延迟回写并清理临时文件（service.SetEnabled(false) 已封装）。
