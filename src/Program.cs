using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IdleAppCloser
{
    internal static class Program
    {
        /// <summary>窗口标题。单实例时也靠它在所有顶层窗口里把自己认出来。</summary>
        public const string WindowTitle = "空闲自动关闭程序";

        public const string Version = "2.0.0";

        private static Icon _appIcon;
        private static Logger _staticLog;

        /// <summary>程序自己的图标（直接从 exe 里取，不需要额外的资源文件）。</summary>
        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { }
                    if (_appIcon == null) _appIcon = SystemIcons.Application;
                }
                return _appIcon;
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // 兜底第一层：任何漏网的异常都必须留下痕迹。
            // 否则一旦闪退，用户和我们能看到的只有“双击了没反应”，完全无从查起。
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { if (_staticLog != null) _staticLog.Error("未处理的异常，程序即将退出: " + e.ExceptionObject); }
                catch { }
            };

            // 兜底第二层：界面线程的异常。
            // WinForms 默认会把它吞掉然后直接关窗口 —— 表现就是“窗口自己没了”，
            // 正是最难排查的那种。这里接过来写日志并告诉用户。
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { if (_staticLog != null) _staticLog.Error("界面线程异常: " + e.Exception); }
                catch { }

                try
                {
                    MessageBox.Show("程序遇到了一个错误：\r\n\r\n" + e.Exception.Message
                        + "\r\n\r\n详细信息已经写进日志文件，程序会继续运行。",
                        WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Run(args);
            }
            catch (Exception ex)
            {
                // 最后的兜底：宁可弹一个难看的框，也不要让程序静默死掉
                try { if (_staticLog != null) _staticLog.Error("启动失败: " + ex); } catch { }
                try
                {
                    MessageBox.Show("程序启动失败：\r\n\r\n" + ex.Message, WindowTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        private static void Run(string[] args)
        {
            bool silent = HasFlag(args, "--silent");     // 开机自启用，直接进托盘不弹窗
            bool check = HasFlag(args, "--check");       // 自检，主要给构建脚本和排查问题用
            string checkOut = GetOption(args, "--check-out");

            string configPath = ResolveConfigPath();
            string logDir = Path.Combine(Path.GetDirectoryName(configPath), "logs");

            // 先用 null 加载配置：日志级别本身来自配置，而读配置的过程又想写日志。
            // 所以顺序必须是“先解析配置 → 再按配置建 Logger”。
            AppConfig cfg;
            try
            {
                cfg = AppConfig.Load(configPath, null);
            }
            catch (Exception ex)
            {
                cfg = new AppConfig();
                cfg.ConfigPath = configPath;
                cfg.Warnings.Add("读取配置失败，已改用默认值: " + ex.Message);
            }

            Logger log = new Logger(logDir, "idlecloser", cfg.LogLevel, cfg.LogMaxKB, cfg.LogKeepDays);
            _staticLog = log;

            // 配置告警统一由 MonitorEngine 在开始监控时记录，这里不重复记一遍

            if (check)
            {
                RunCheck(cfg, log, configPath, checkOut);
                return;
            }

            // ---- 单实例 ----
            // 用户可能双击了两次，或者“开机自启 + 手动打开”撞在一起。
            bool createdNew;
            Mutex mutex = new Mutex(true, @"Local\IdleAppCloser_SingleInstance", out createdNew);

            if (!createdNew)
            {
                if (!silent)
                {
                    // 手动双击时，尽量把已经开着的那个窗口叫到前面来，而不是干巴巴地报错
                    if (!ActivateExistingWindow())
                    {
                        MessageBox.Show(
                            "程序已经在运行了。\r\n\r\n" +
                            "它就在屏幕右下角的托盘里（可能被折叠在 ∧ 箭头里面），双击那个图标就能打开设置。",
                            WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                return;
            }

            try
            {
                // 会话 0 是服务会话，没有桌面，读不到键盘鼠标的空闲状态，留着它只会误判
                if (Process.GetCurrentProcess().SessionId == 0)
                {
                    log.Error("当前运行在会话 0（服务会话），无法读取键鼠空闲状态，程序退出。");
                    if (!silent)
                    {
                        MessageBox.Show(
                            "这个程序必须运行在正常登录的桌面里，不能作为系统服务启动。",
                            WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                log.Info("========================================================");
                log.Info(WindowTitle + " v" + Version + " 启动"
                    + (silent ? "（开机自启，静默模式）" : string.Empty));

                // 之前开过开机自启、但程序被挪了位置的话，顺手把启动项里的路径纠正过来。
                // 否则用户换个文件夹放之后就再也不自启了，而且完全找不到原因。
                if (AutoStart.IsEnabled)
                {
                    try { AutoStart.Refresh(Application.ExecutablePath, true); }
                    catch (Exception ex) { log.Warn("更新开机启动项路径失败: " + ex.Message); }
                }

                using (AppHost host = new AppHost(cfg, log, silent))
                {
                    Application.Run(host);
                }
                log.Info("消息循环结束，程序正常退出。");
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Close();
            }
        }

        // ------------------------------------------------------------------ 自检

        private static void RunCheck(AppConfig cfg, Logger log, string configPath, string checkOut)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(WindowTitle + " v" + Version + " —— 自检");
            sb.AppendLine();
            sb.AppendLine("配置文件: " + configPath);
            sb.AppendLine("日志目录: " + log.Folder);
            sb.AppendLine("目标程序: " + (cfg.TargetNames.Count > 0
                ? string.Join(", ", cfg.TargetNames.ToArray())
                : (cfg.TargetPaths.Count > 0 ? string.Join("; ", cfg.TargetPaths.ToArray()) : "(未设置)")));
            sb.AppendLine("空闲阈值: " + cfg.IdleSeconds + " 秒");
            sb.AppendLine("关闭方式: " + (cfg.CloseMode == CloseMode.Graceful
                ? "graceful（先请程序自己关闭，" + cfg.GracefulTimeoutSeconds + " 秒后强杀）"
                : "force（直接强制结束）"));
            sb.AppendLine("开机自启: " + (AutoStart.IsEnabled ? "已开启" : "未开启"));
            sb.AppendLine();

            try
            {
                IdleState idle = new IdleMonitor(false).Poll(cfg.IdleSeconds);

                sb.AppendLine("实时诊断：");
                sb.AppendLine("  会话 ID: " + Process.GetCurrentProcess().SessionId);
                sb.AppendLine("  当前已空闲: " + ((int)idle.IdleSeconds) + " 秒");
                if (idle.IsLocked) sb.AppendLine("  当前状态: 已锁屏");

                string matchMode;
                var found = ProcessCloser.FindTargets(cfg, log, out matchMode);
                sb.AppendLine("  匹配方式: " + matchMode);

                if (found.Count == 0)
                {
                    sb.AppendLine("  匹配到的目标进程: (无) ← 如果它确实正在运行，说明程序名写错了");
                }
                else
                {
                    sb.AppendLine("  匹配到的目标进程: " + found.Count + " 个");
                    foreach (TargetProcess t in found)
                        sb.AppendLine("    - " + t.Name + "  (PID " + t.Pid + ")");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("实时诊断失败: " + ex.Message);
            }

            string text = sb.ToString();

            if (!string.IsNullOrEmpty(checkOut))
            {
                try { File.WriteAllText(checkOut, text, new UTF8Encoding(true)); }
                catch { }
            }

            // 无窗口程序直接运行时没有控制台，这行输出会被丢掉；
            // 但被构建脚本重定向时是有用的，所以留着。
            try { Console.WriteLine(text); }
            catch { }
        }

        // ------------------------------------------------------------------ 路径

        /// <summary>
        /// 决定设置文件放哪儿。
        /// 优先放 exe 同目录（绿色版：整个文件夹拷走，设置跟着一起走）。
        /// 万一程序被放在 Program Files 这类只读位置，就退到用户目录，保证一定能用。
        /// </summary>
        private static string ResolveConfigPath()
        {
            string exeDir;
            try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); }
            catch { exeDir = null; }

            if (!string.IsNullOrEmpty(exeDir))
            {
                string local = Path.Combine(exeDir, "config.ini");
                if (File.Exists(local) || CanWriteTo(exeDir)) return local;
            }

            string fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleAppCloser");
            try { Directory.CreateDirectory(fallbackDir); } catch { }
            return Path.Combine(fallbackDir, "config.ini");
        }

        private static bool CanWriteTo(string dir)
        {
            string probe = Path.Combine(dir, ".writetest-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { }
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ 参数

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    return (i + 1 < args.Length) ? args[i + 1] : string.Empty;

                string prefix = name + "=";
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length);
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------ 唤起已有窗口

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        /// <summary>
        /// 找到已有实例的窗口并把它显示到最前面。
        /// 用标题匹配而不是 FindWindow：窗口可能已经被收进托盘（隐藏了），
        /// FindWindow 找不到隐藏窗口，而 EnumWindows 能。
        /// </summary>
        private static bool ActivateExistingWindow()
        {
            IntPtr found = IntPtr.Zero;

            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    StringBuilder sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    if (sb.ToString() == WindowTitle)
                    {
                        found = hWnd;
                        return false;   // 找到了，停止枚举
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                return false;
            }

            if (found == IntPtr.Zero) return false;

            try
            {
                ShowWindow(found, SW_SHOW);
                ShowWindow(found, SW_RESTORE);
                SetForegroundWindow(found);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
