using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipImageToPath;

/// <summary>
/// 托盘生命周期与自检流程：持有 NotifyIcon，退出时释放剪贴板监听器。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int SelfTestStartDelayMs = 600;     // 等待消息泵稳定后再注入测试图片
    private const int SelfTestPollIntervalMs = 100;
    private const int SelfTestMaxAttempts = 50;       // 5 秒超时，超出视为自检失败
    private const string AutoStartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "ClipImageToPath";
    private const string SettingsKeyPath = @"Software\ClipImageToPath";
    private const string ShowBalloonValueName = "ShowBalloonTip";

    private readonly ClipboardMonitor _monitor;
    private readonly bool _selftest;
    private NotifyIcon? _trayIcon;
    private System.Windows.Forms.Timer? _selfTestTimer;
    private bool _selfTestStarted;
    private bool _cleaned;
    private string? _balloonPath; // 最近一次转换的路径，气泡点击跳转时使用

    /// <summary>自检结果码，供 Main 返回给调用方。</summary>
    public int ExitCode { get; private set; }

    /// <summary>
    /// 构造函数。
    /// 参数:
    ///     monitor: 剪贴板监听器，负责在退出时反注册系统钩子
    ///     selftest: true 时进入自检流程，不显示托盘
    /// </summary>
    public TrayApplicationContext(ClipboardMonitor monitor, bool selftest)
    {
        _monitor = monitor;
        _selftest = selftest;
        // 依赖事件而非重写虚方法：退出消息泵时同步释放托盘与剪贴板监听
        ThreadExit += (_, _) => Cleanup();

        if (!selftest)
        {
            CreateTrayIcon();
        }
        else
        {
            // 自检模式不显示托盘，避免测试期间出现闪烁图标
            AttachConsole(AttachParentProcess); // 有父控制台时输出到 stdout；双击运行时失败无影响
            _selfTestTimer = new System.Windows.Forms.Timer { Interval = SelfTestStartDelayMs };
            _selfTestTimer.Tick += (_, _) => RunSelfTest();
            _selfTestTimer.Start();
        }
    }

    /// <summary>
    /// 创建托盘图标与右键菜单（开机自启、退出）。
    /// </summary>
    private void CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        // [修改] 开机自启默认勾选：首次运行自动写入 Run 键并勾选菜单，用户可随时取消
        var autoStartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true, // 点击时自动切换勾选，处理器按新状态写注册表
            Checked = EnableAutoStartByDefault(),
        };
        autoStartItem.Click += (_, _) => ToggleAutoStart(autoStartItem);
        // [修改] 新增消息提示开关：控制图片转换成功后的气泡提示
        var balloonItem = new ToolStripMenuItem("消息提示")
        {
            CheckOnClick = true, // 点击时自动切换勾选，处理器按新状态写注册表
            Checked = IsBalloonEnabled(),
        };
        balloonItem.Click += (_, _) => ToggleBalloon(balloonItem);
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitThread();
        menu.Items.Add(autoStartItem);
        menu.Items.Add(balloonItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "ClipImageToPath - 剪贴板图片自动转为路径",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // [修改] 气泡点击时在资源管理器中定位临时文件
        _trayIcon.BalloonTipClicked += (_, _) => OpenBalloonPath();
        _trayIcon.ShowBalloonTip(2000, "ClipImageToPath", "剪贴板中的图片将自动保存为临时文件并替换为路径", ToolTipIcon.Info);
    }

    /// <summary>
    /// 从嵌入资源加载程序图标。
    /// 返回:
    ///     Icon，多尺寸图标，供托盘使用
    /// </summary>
    private static Icon LoadAppIcon()
    {
        // 先复制到独立流再构造 Icon，确保资源流关闭后图标数据仍有效
        using var stream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("ClipImageToPath.Assets.app.ico")
            ?? throw new InvalidOperationException("嵌入图标资源缺失：ClipImageToPath.Assets.app.ico");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return new Icon(buffer);
    }

    /// <summary>
    /// 判断当前程序是否已注册开机自启。
    /// 返回:
    ///     bool，Run 键中同名值与本程序路径一致时为 true
    /// </summary>
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRunKeyPath);
            return key?.GetValue(AutoStartValueName) is string value
                && string.Equals(value, AutoStartCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读注册表失败按未启用处理，避免托盘菜单因异常无法显示
            return false;
        }
    }

    /// <summary>
    /// 确保开机自启默认启用：已注册则直接返回勾选状态，未注册则写入 Run 键。
    /// 返回:
    ///     bool，开机自启是否已启用
    /// </summary>
    private static bool EnableAutoStartByDefault()
    {
        if (IsAutoStartEnabled())
        {
            return true;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoStartRunKeyPath, writable: true);
            key.SetValue(AutoStartValueName, AutoStartCommand, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            // 默认启用失败时明确提示，避免用户以为已勾选但实际未生效
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "ClipImageToPath", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>
    /// 切换开机自启：勾选时写入当前用户 Run 键，取消时删除同名值。
    /// 参数:
    ///     item: 触发点击的菜单项，其 Checked 已由 CheckOnClick 切换
    /// </summary>
    private static void ToggleAutoStart(ToolStripMenuItem item)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoStartRunKeyPath, writable: true);
            if (item.Checked)
            {
                key.SetValue(AutoStartValueName, AutoStartCommand, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            // 注册表写入失败（权限等）时回滚勾选并明确提示，避免菜单状态与实际不一致
            item.Checked = !item.Checked;
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "ClipImageToPath", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>开机自启注册表值：带引号的 exe 全路径，防止路径含空格时启动失败。</summary>
    private static string AutoStartCommand => $"\"{Environment.ProcessPath}\"";

    /// <summary>
    /// 读取消息提示开关状态，未配置时默认开启。
    /// 返回:
    ///     bool，是否启用消息提示
    /// </summary>
    private static bool IsBalloonEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            object? raw = key?.GetValue(ShowBalloonValueName);
            return raw is int value ? value != 0 : true;
        }
        catch
        {
            // 读注册表失败按默认开启处理，避免托盘菜单因异常无法显示
            return true;
        }
    }

    /// <summary>
    /// 切换消息提示开关并写入注册表。
    /// 参数:
    ///     item: 触发点击的菜单项，其 Checked 已由 CheckOnClick 切换
    /// </summary>
    private static void ToggleBalloon(ToolStripMenuItem item)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key.SetValue(ShowBalloonValueName, item.Checked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            // 注册表写入失败时回滚勾选并明确提示，避免菜单状态与实际不一致
            item.Checked = !item.Checked;
            MessageBox.Show($"设置消息提示失败：{ex.Message}", "ClipImageToPath", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 图片转换成功后的气泡提示：开关开启且托盘存在时显示，并记录路径供点击跳转。
    /// 参数:
    ///     sender: 事件源（ImageToPathService）
    ///     path: 转换后的临时 PNG 完整路径
    /// </summary>
    public void OnPathConverted(object? sender, string path)
    {
        if (!IsBalloonEnabled() || _trayIcon == null)
        {
            return;
        }
        _balloonPath = path;
        // 路径可能超过气泡正文长度上限，截断展示，完整路径在日志中
        string display = path.Length <= 120 ? path : path[..120] + "...";
        _trayIcon.ShowBalloonTip(3000, "ClipImageToPath", $"图片已转换为路径：{display}", ToolTipIcon.Info);
    }

    /// <summary>
    /// 气泡点击跳转：在资源管理器中定位最近转换的临时文件。
    /// </summary>
    private void OpenBalloonPath()
    {
        if (string.IsNullOrEmpty(_balloonPath) || !File.Exists(_balloonPath))
        {
            return;
        }
        try
        {
            // /select 参数让资源管理器直接定位并选中目标文件
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_balloonPath}\"");
        }
        catch (Exception ex)
        {
            // 跳转失败仅记录日志，不影响主流程
            Logger.Error($"打开临时文件位置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 端到端自检：写入测试图片 → 等待监听链路转成路径 → 校验临时文件存在。
    /// </summary>
    private void RunSelfTest()
    {
        if (_selfTestStarted)
        {
            return;
        }
        _selfTestStarted = true;
        _selfTestTimer?.Stop();

        string? tempPath = null;
        try
        {
            // 生成纯色测试图写入剪贴板，走与真实复制完全相同的监听链路
            using (var bitmap = new Bitmap(64, 64))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Red);
                Clipboard.SetImage(bitmap);
            }

            for (int i = 0; i < SelfTestMaxAttempts; i++)
            {
                // 轮询期间必须泵窗口消息，否则 WM_CLIPBOARDUPDATE 一直排队，监听链路无法执行
                Application.DoEvents();
                if (Clipboard.ContainsText())
                {
                    tempPath = Clipboard.GetText();
                    // 校验回写内容确实是刚生成的临时 PNG 全路径
                    if (!string.IsNullOrWhiteSpace(tempPath) &&
                        tempPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(tempPath))
                    {
                        FinishSelfTest(true, $"自检通过：图片已转为路径 {tempPath}");
                        return;
                    }
                }
                Thread.Sleep(SelfTestPollIntervalMs);
            }

            FinishSelfTest(false, $"自检失败：5 秒内未将图片转为路径（当前剪贴板：{Clipboard.GetText() ?? "(无文本)"}）");
        }
        catch (Exception ex)
        {
            FinishSelfTest(false, $"自检异常：{ex}");
        }
        finally
        {
            // 清理测试文件与测试剪贴板内容，避免污染用户环境
            try
            {
                if (tempPath != null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 清理失败不影响自检结论，文件本身在临时目录内
            }
            try
            {
                Clipboard.Clear();
            }
            catch
            {
                // 剪贴板被占用时忽略，测试进程随后退出
            }
        }
    }

    /// <summary>
    /// 输出自检结果、记录日志并退出消息泵。
    /// 参数:
    ///     success: 是否成功
    ///     message: 结果描述
    /// </summary>
    private void FinishSelfTest(bool success, string message)
    {
        ExitCode = success ? 0 : 1;
        if (success)
        {
            Logger.Info(message);
        }
        else
        {
            Logger.Error(message);
        }
        try
        {
            Console.WriteLine($"[ClipImageToPath] {message}");
            Console.Out.Flush();
        }
        catch
        {
            // 无可用控制台时以日志和退出码为准
        }
        ExitThread();
    }

    /// <summary>
    /// 释放托盘图标与剪贴板监听器，防止进程退出后残留系统钩子。
    /// </summary>
    private void Cleanup()
    {
        if (_cleaned)
        {
            return;
        }
        _cleaned = true;
        _selfTestTimer?.Stop();
        _selfTestTimer?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }
        _monitor.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);
}
