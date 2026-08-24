using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClipImageToPath;

/// <summary>
/// 剪贴板图片转路径服务：判图、保存临时 PNG、回写剪贴板为全路径文本。
/// </summary>
internal sealed class ImageToPathService
{
    private const int MaxClipboardRetry = 5;      // 剪贴板被其他进程占用时最多重试次数
    private const int ClipboardRetryDelayMs = 80; // 重试间隔，避免抢占时忙等

    /// <summary>
    /// 剪贴板更新事件入口：含图片则转存为临时路径并回写，其他格式一律忽略。
    /// 参数:
    ///     sender: 事件源（ClipboardMonitor）
    ///     e: 空事件参数
    /// </summary>
    public void OnClipboardUpdated(object? sender, EventArgs e)
    {
        try
        {
            HandleClipboardUpdate();
        }
        catch (Exception ex)
        {
            // 处理失败必须显式落日志而不是静默吞掉
            Logger.Error("剪贴板图片处理失败", ex);
        }
    }

    /// <summary>
    /// 核心流程：判断是否图片 → 读取 → 存临时文件 → 回写路径。
    /// </summary>
    private void HandleClipboardUpdate()
    {
        // 回写路径文本后系统会再次通知，此时非图片格式直接短路，天然避免处理死循环
        if (!Clipboard.ContainsImage())
        {
            return;
        }

        using (var image = GetImageWithRetry())
        {
            if (image == null)
            {
                Logger.Error("剪贴板含图片格式但读取返回空");
                return;
            }

            string path = BuildTempPath();
            // PNG 无损且各程序粘贴兼容性最好，作为统一落盘格式
            image.Save(path, ImageFormat.Png);
            SetTextWithRetry(path);
            Logger.Info($"图片已转为临时路径：{path}");
        }
    }

    /// <summary>
    /// 读取剪贴板图片，被占用时按固定间隔重试。
    /// 返回:
    ///     Image?，读取到的图片；重试耗尽后抛异常
    /// </summary>
    private static Image? GetImageWithRetry()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Clipboard.GetImage();
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                if (attempt >= MaxClipboardRetry - 1)
                {
                    throw;
                }
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// 回写文本到剪贴板，被占用时按固定间隔重试。
    /// 参数:
    ///     text: 要写入剪贴板的路径文本
    /// </summary>
    private static void SetTextWithRetry(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                if (attempt >= MaxClipboardRetry - 1)
                {
                    throw;
                }
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// 生成唯一临时文件路径：%TEMP% 下 clip_时间_随机串.png。
    /// 返回:
    ///     string，完整文件路径
    /// </summary>
    private static string BuildTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"clip_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.png");
    }

    /// <summary>
    /// 判断剪贴板异常是否值得重试（占用/COM 失败），编程错误类异常不重试以免掩盖 bug。
    /// 参数:
    ///     ex: 捕获的异常
    /// 返回:
    ///     bool，是否可重试
    /// </summary>
    private static bool IsRetryable(Exception ex)
    {
        return ex is ExternalException or COMException or SEHException;
    }
}
