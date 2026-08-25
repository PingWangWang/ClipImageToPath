using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClipImageToPath;

/// <summary>
/// 程序入口：组装剪贴板监听器、托盘与消息泵，支持 --selftest 端到端自检与 --version 输出版本。
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\ClipImageToPath_SingleInstance";
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>
    /// 入口方法。
    /// 参数:
    ///     args: 命令行参数，含 --version / --selftest 时走对应分支
    /// 返回:
    ///     int，0=正常或自检通过，1=启动或自检失败，2=已有实例在运行
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.Exists(args, a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            // WinExe 无自带控制台，先挂到父进程控制台才能让版本输出可见
            AttachConsole(AttachParentProcess);
            Console.WriteLine($"ClipImageToPath {GetVersion()}");
            return 0;
        }

        bool selftest = Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        try
        {
            // 单实例互斥：剪贴板处理带副作用，两个实例同时回写会互相覆盖
            using var singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                Logger.Info("检测到已有实例在运行，本实例退出");
                return 2;
            }

            // 剪贴板 API 依赖 STA 线程与消息泵，必须在创建任何控件前完成初始化
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var monitor = new ClipboardMonitor();
            // [修改] 延迟回写期间持有定时器，using 保证消息泵退出后资源释放
            using var service = new ImageToPathService();
            monitor.ClipboardUpdated += service.OnClipboardUpdated;

            using var context = new TrayApplicationContext(monitor, selftest);
            // [修改] 图片转换成功后由托盘弹气泡提示
            service.PathConverted += context.OnPathConverted;
            Application.Run(context);
            return context.ExitCode;
        }
        catch (Exception ex)
        {
            Logger.Error("程序启动失败", ex);
            return 1;
        }
    }

    /// <summary>
    /// 读取程序集信息版本（与 csproj 的 Version 属性一致，可带预发布后缀）。
    /// 返回:
    ///     string，版本号文本
    /// </summary>
    private static string GetVersion()
    {
        var attribute = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attribute?.InformationalVersion ?? "unknown";
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);
}
