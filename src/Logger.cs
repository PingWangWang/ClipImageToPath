using System;
using System.IO;
using System.Text;

namespace ClipImageToPath;

/// <summary>
/// 极简文件日志：写入 %TEMP%\ClipImageToPath\clip.log，失败时静默（日志不能影响主流程）。
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(Path.GetTempPath(), "ClipImageToPath");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "clip.log");

    /// <summary>记录一条信息级日志。</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>记录一条错误级日志。</summary>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>记录一条含异常细节的错误日志。</summary>
    public static void Error(string message, Exception exception) => Write("ERROR", $"{message}：{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不能中断剪贴板处理
        }
    }
}
