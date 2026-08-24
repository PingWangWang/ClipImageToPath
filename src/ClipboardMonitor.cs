using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipImageToPath;

/// <summary>
/// 基于 AddClipboardFormatListener 的剪贴板事件监听器：隐藏窗口接收 WM_CLIPBOARDUPDATE 并转发事件。
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    private bool _disposed;

    /// <summary>剪贴板内容发生变化时触发（含图片、文本、文件等所有格式）。</summary>
    public event EventHandler? ClipboardUpdated;

    /// <summary>
    /// 构造函数：创建隐藏消息窗口并注册剪贴板监听。
    /// </summary>
    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Caption = "ClipImageToPathClipboardListener" });
        // 注册失败说明系统不支持该 API（Win7 以下），启动即快速失败而不是静默失效
        if (!AddClipboardFormatListener(Handle))
        {
            throw new InvalidOperationException("AddClipboardFormatListener 注册失败，需要 Windows 7 及以上系统");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            m.Result = IntPtr.Zero;
            try
            {
                ClipboardUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 事件处理器的异常不能冒泡到 WndProc，否则进程直接终止
                Debug.WriteLine($"剪贴板更新处理异常：{ex}");
            }
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// 反注册剪贴板监听并销毁隐藏窗口。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
