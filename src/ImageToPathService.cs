using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace ClipImageToPath;

/// <summary>
/// 剪贴板图片转路径服务：判图、保存临时 PNG、回写剪贴板为全路径文本。
/// </summary>
internal sealed class ImageToPathService : IDisposable
{
    private const int MaxClipboardRetry = 5;      // 剪贴板被其他进程占用时最多重试次数
    private const int ClipboardRetryDelayMs = 80; // 重试间隔，避免抢占时忙等
    private const int WriteDelayMs = 500;         // 回写延迟：等待剪贴板历史服务完成图片快照，避免图片记录被路径覆盖
    private const int RepeatIgnoreMs = 800;       // 同一次复制的重复通知间隔上限：OLE 提交会广播多次剪贴板更新，短间隔视为同一事件
    private const int MaxFingerprintCount = 500;  // 去重缓存上限：仅记录进程运行期间处理过的图片，防止缓存无限增长

    private readonly System.Windows.Forms.Timer _writeTimer; // 单发定时器，同一时刻最多一个待回写任务
    private string? _pendingPath;                            // 等待回写的临时 PNG 路径
    private bool _disposed;
    private readonly Dictionary<string, DateTime> _recentFingerprints = new(); // 已生成路径的图片指纹 → 首次处理时间，用于进程内永久去重

    /// <summary>图片成功转换为路径后触发，参数为临时 PNG 完整路径。</summary>
    public event EventHandler<string>? PathConverted;

    /// <summary>功能总开关：false 时停止将剪贴板图片转为路径（仅短回路，监听仍运行）。默认开启。</summary>
    public bool Enabled { get; private set; } = true;

    /// <summary>
    /// 设置功能总开关；关闭时取消挂起的延迟回写并清理临时文件，保持剪贴板图片状态。
    /// 参数:
    ///     enabled: 是否启用图片转路径功能
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            // 关闭瞬间若有待回写的延迟任务，立即作废并清理临时文件，避免关闭后仍写出路径
            CancelPendingWrite();
        }
    }

    /// <summary>
    /// 构造函数：初始化延迟回写定时器。
    /// </summary>
    public ImageToPathService()
    {
        // 单发模式：新图片事件重启定时器，旧回写任务自动作废，避免并发覆盖
        _writeTimer = new System.Windows.Forms.Timer { Interval = WriteDelayMs };
        _writeTimer.Tick += WritePendingPath;
    }

    /// <summary>
    /// 释放延迟回写定时器，防止消息泵退出后残留回调。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _writeTimer.Stop();
        _writeTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 剪贴板更新事件入口：含图片则转存为临时路径并回写，其他格式一律忽略。
    /// 参数:
    ///     sender: 事件源（ClipboardMonitor）
    ///     e: 空事件参数
    /// </summary>
    public void OnClipboardUpdated(object? sender, EventArgs e)
    {
        // 功能关闭时直接短路，既不转换也不产生副作用
        if (!Enabled)
        {
            return;
        }
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
            // [修改] 落盘后先做指纹去重：同一图片仅在首次复制时生成路径，
            //       之后无论从 Win+V 历史点击、粘贴后应用回写还是重新复制，都保持图片不再生成路径
            string fingerprint = ComputeFingerprint(path);
            if (TryGetLastSeen(fingerprint, out DateTime lastSeen))
            {
                // [修改] 短间隔重复是 OLE 同一次复制产生的多次通知，跳过即可并保留挂起的回写；
                //       长间隔重复（历史点击恢复/重新复制同一图片）取消回写并保持图片
                if (DateTime.UtcNow - lastSeen < TimeSpan.FromMilliseconds(RepeatIgnoreMs))
                {
                    _recentFingerprints[fingerprint] = DateTime.UtcNow;
                    TryDeleteFile(path);
                    return;
                }
                CancelPendingWrite();
                TryDeleteFile(path);
                Logger.Info("检测到重复图片，保持剪贴板图片状态");
                return;
            }
            RecordFingerprint(fingerprint);
            // [修改] 不再立即回写路径：延迟写入让剪贴板历史（Win+V）先完成图片快照，
            //       避免异步监听尚未读取图片就被 EmptyClipboard 销毁，导致历史里只留下路径一条记录
            ScheduleDelayedWrite(path);
        }
    }

    /// <summary>
    /// 计算临时 PNG 文件的 SHA256 指纹，用于识别同一图片的重复出现。
    /// 参数:
    ///     path: 已保存的临时 PNG 路径
    /// 返回:
    ///     string，十六进制 SHA256 摘要
    /// </summary>
    private static string ComputeFingerprint(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>
    /// 查询图片指纹的最近出现时间。
    /// 参数:
    ///     fingerprint: 图片 SHA256 指纹
    ///     lastSeen: 输出参数，最近出现时间
    /// 返回:
    ///     bool，指纹已处理过则为 true
    /// </summary>
    private bool TryGetLastSeen(string fingerprint, out DateTime lastSeen)
    {
        return _recentFingerprints.TryGetValue(fingerprint, out lastSeen);
    }

    /// <summary>
    /// 记录或刷新图片指纹的最近出现时间，超出缓存上限时淘汰最旧指纹。
    /// 参数:
    ///     fingerprint: 图片 SHA256 指纹
    /// </summary>
    private void RecordFingerprint(string fingerprint)
    {
        _recentFingerprints[fingerprint] = DateTime.UtcNow;
        if (_recentFingerprints.Count > MaxFingerprintCount)
        {
            // 淘汰最早处理的指纹，限制进程运行期间缓存体积
            string oldest = _recentFingerprints.OrderBy(kv => kv.Value).First().Key;
            _recentFingerprints.Remove(oldest);
        }
    }

    /// <summary>
    /// 取消挂起的延迟回写并清理对应临时文件，保持剪贴板图片状态。
    /// </summary>
    private void CancelPendingWrite()
    {
        _writeTimer.Stop();
        if (_pendingPath != null)
        {
            TryDeleteFile(_pendingPath);
            _pendingPath = null;
        }
    }

    /// <summary>
    /// 删除临时文件，失败时静默（文件可能已被占用或已清理）。
    /// 参数:
    ///     path: 要删除的文件路径
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程，临时目录由系统回收
        }
    }

    /// <summary>
    /// 延迟回写调度：落盘后保持剪贴板图片状态，等待历史服务保存图片后再写入路径。
    /// 参数:
    ///     path: 已保存的临时 PNG 完整路径
    /// </summary>
    private void ScheduleDelayedWrite(string path)
    {
        _pendingPath = path;
        // 重启定时器：延迟窗口内再次复制图片时，旧回写任务由新事件接管，保证写入的始终是最新图片路径
        _writeTimer.Stop();
        _writeTimer.Start();
    }

    /// <summary>
    /// 延迟到期后的回写处理：剪贴板仍是图片时才写入路径，否则放弃并清理临时文件。
    /// 参数:
    ///     sender: 定时器事件源
    ///     e: 事件参数（未使用）
    /// </summary>
    private void WritePendingPath(object? sender, EventArgs e)
    {
        _writeTimer.Stop();
        try
        {
            // 延迟期间用户可能已复制其他内容，剪贴板不再是图片时放弃回写，避免覆盖用户新操作
            if (!Clipboard.ContainsImage())
            {
                if (_pendingPath != null)
                {
                    TryDeleteFile(_pendingPath);
                }
                Logger.Info("剪贴板内容已变化，取消本次图片路径回写");
                _pendingPath = null;
                return;
            }

            string path = _pendingPath ?? throw new InvalidOperationException("延迟回写路径为空");
            SetTextWithRetry(path);
            Logger.Info($"图片已转为临时路径：{path}");
            _pendingPath = null;
            // [修改] 转换成功后通知 UI 层弹气泡提示，UI 层自行判断开关状态
            PathConverted?.Invoke(this, path);
        }
        catch (Exception ex)
        {
            // 定时器回调的异常不能冒泡到消息泵，否则会终止消息循环
            Logger.Error("剪贴板图片延迟回写失败", ex);
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
