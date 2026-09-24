using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace IdleAppCloser
{
    internal sealed class TargetProcess
    {
        public int Pid;
        public string Name = string.Empty;
        public string Path = string.Empty;
        public DateTime StartTime = DateTime.MinValue;
        public bool StartTimeKnown;

        /// <summary>已经活了多少秒；取不到启动时间时退化为 0（交由调用方用本地观察时间兜底）。</summary>
        public double LifetimeSeconds
        {
            get
            {
                if (!StartTimeKnown) return -1;
                return (DateTime.Now - StartTime).TotalSeconds;
            }
        }
    }

    /// <summary>
    /// 目标进程的查找、保护规则判定与关闭。
    /// </summary>
    internal static class ProcessCloser
    {
        // ------------------------------------------------------------ Win32

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint WM_CLOSE = 0x0010;

        // ------------------------------------------------------------ 查找

        /// <summary>进程名规范化：去掉 .exe 后缀并小写，方便比较。</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Trim().Trim('"');
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\').ToLowerInvariant(); }
            catch { return path.Trim().Trim('"').TrimEnd('\\').ToLowerInvariant(); }
        }

        /// <summary>取进程完整路径。优先用 QueryFullProcessImageName（对权限受限、跨位数更友好）。</summary>
        public static string GetProcessPath(Process p)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024);
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size))
                        return sb.ToString();
                }
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
            }

            try { return p.MainModule != null ? p.MainModule.FileName : string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>按配置找出当前所有需要监控的目标进程。</summary>
        public static List<TargetProcess> FindTargets(AppConfig cfg, Logger log, out string matchMode)
        {
            var result = new List<TargetProcess>();
            bool byPath = cfg.TargetPaths.Count > 0;
            matchMode = byPath ? "路径精确匹配" : "进程名匹配";

            var wantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in cfg.TargetNames) wantedNames.Add(NormalizeName(n));

            var wantedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in cfg.TargetPaths) wantedPaths.Add(NormalizePath(p));

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex)
            {
                log.Error("枚举进程失败: " + ex.Message, ex);
                return result;
            }

            foreach (Process p in all)
            {
                try
                {
                    string name = NormalizeName(p.ProcessName);
                    bool hit;
                    string fullPath = null;

                    if (byPath)
                    {
                        fullPath = GetProcessPath(p);
                        hit = fullPath.Length > 0 && wantedPaths.Contains(NormalizePath(fullPath));
                    }
                    else
                    {
                        hit = wantedNames.Contains(name);
                    }

                    if (!hit) continue;
                    if (p.Id == Process.GetCurrentProcess().Id) continue;   // 绝不关自己

                    var t = new TargetProcess();
                    t.Pid = p.Id;
                    t.Name = p.ProcessName;
                    t.Path = fullPath ?? GetProcessPath(p);
                    try
                    {
                        t.StartTime = p.StartTime;
                        t.StartTimeKnown = true;
                    }
                    catch
                    {
                        t.StartTimeKnown = false;   // 权限不足时取不到，交给调用方兜底
                    }
                    result.Add(t);
                }
                catch
                {
                    // 进程可能刚好退出，忽略
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return result;
        }

        /// <summary>找出正在运行的“保护进程”（它们一出现就完全不允许关闭目标）。</summary>
        public static List<string> FindRunningProtected(AppConfig cfg)
        {
            var found = new List<string>();
            if (cfg.ProtectedProcesses.Count == 0) return found;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in cfg.ProtectedProcesses) wanted.Add(NormalizeName(n));

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return found; }

            foreach (Process p in all)
            {
                try
                {
                    if (wanted.Contains(NormalizeName(p.ProcessName)))
                    {
                        if (!found.Contains(p.ProcessName)) found.Add(p.ProcessName);
                    }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return found;
        }

        /// <summary>目标进程是否正占据前台全屏窗口（看视频、做演示时应跳过）。</summary>
        public static bool HasFullscreenWindow(int pid)
        {
            bool found = false;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint wpid;
                    GetWindowThreadProcessId(hWnd, out wpid);
                    if (wpid != (uint)pid) return true;       // 不是目标进程，继续
                    if (!IsWindowVisible(hWnd)) return true;  // 只看可见窗口
                    if (GetWindowTextLength(hWnd) == 0) return true; // 无标题的多是辅助窗口

                    RECT r;
                    if (!GetWindowRect(hWnd, out r)) return true;

                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w <= 0 || h <= 0) return true;

                    foreach (Screen s in Screen.AllScreens)
                    {
                        // 允许几像素误差，有些程序全屏时会比屏幕大一点点
                        if (Math.Abs(r.Left - s.Bounds.Left) <= 2 && Math.Abs(r.Top - s.Bounds.Top) <= 2 &&
                            Math.Abs(w - s.Bounds.Width) <= 4 && Math.Abs(h - s.Bounds.Height) <= 4)
                        {
                            found = true;
                            return false;   // 找到了，停止枚举
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        // ------------------------------------------------------------ 关闭

        /// <summary>向目标进程的所有可见顶层窗口投递 WM_CLOSE（比 CloseMainWindow 更彻底）。</summary>
        private static int PostCloseToWindows(int pid)
        {
            int count = 0;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint wpid;
                    GetWindowThreadProcessId(hWnd, out wpid);
                    if (wpid != (uint)pid) return true;
                    if (!IsWindowVisible(hWnd)) return true;
                    try
                    {
                        if (PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) count++;
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return count;
        }

        /// <summary>
        /// 用 CreateToolhelp32Snapshot 取整棵进程树，返回指定进程的所有后代 PID。
        /// 不依赖 WMI，避免 System.Management 在受限环境下失败。
        /// </summary>
        public static List<int> GetDescendantPids(int rootPid)
        {
            var childrenOf = new Dictionary<int, List<int>>();
            IntPtr snap = IntPtr.Zero;
            try
            {
                snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new List<int>();

                var pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(snap, ref pe))
                {
                    do
                    {
                        int child = (int)pe.th32ProcessID;
                        int parent = (int)pe.th32ParentProcessID;
                        if (child <= 0 || parent <= 0) continue;

                        List<int> list;
                        if (!childrenOf.TryGetValue(parent, out list))
                        {
                            list = new List<int>();
                            childrenOf[parent] = list;
                        }
                        list.Add(child);

                        pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                    }
                    while (Process32Next(snap, ref pe));
                }
            }
            catch { }
            finally
            {
                if (snap != IntPtr.Zero && snap != new IntPtr(-1))
                {
                    try { CloseHandle(snap); } catch { }
                }
            }

            // 广度优先收集所有后代（深度优先也一样，这里顺便保证“先子后父”的结束顺序）
            var result = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            var guard = new HashSet<int>();
            guard.Add(rootPid);

            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                List<int> kids;
                if (!childrenOf.TryGetValue(cur, out kids)) continue;
                foreach (int k in kids)
                {
                    if (!guard.Add(k)) continue;      // 防御 PID 复用造成的假环
                    result.Add(k);
                    queue.Enqueue(k);
                }
            }

            result.Reverse();   // 反过来：先结束最深的子进程，再结束父进程
            return result;
        }

        /// <summary>
        /// 关闭一个目标进程。返回 true 表示确实已经退出。
        /// graceful 模式会先请求正常退出（给目标保存数据的机会），超时再强杀。
        /// </summary>
        public static bool Terminate(TargetProcess target, AppConfig cfg, Logger log, Func<bool> shouldAbort)
        {
            Process p = null;
            try { p = Process.GetProcessById(target.Pid); }
            catch { return true; }   // 已经不存在了

            try
            {
                bool graceful = cfg.CloseMode == CloseMode.Graceful;

                // 先处理子进程：很多主程序（启动器壳）需要连子进程一起结束才算真的关掉
                var descendants = new List<int>();
                if (cfg.KillChildProcesses)
                {
                    descendants = GetDescendantPids(target.Pid);
                    if (descendants.Count > 0)
                        log.Info(string.Format("PID {0} ({1}) 有 {2} 个子进程，将一并结束。", target.Pid, target.Name, descendants.Count));
                }

                if (graceful)
                {
                    int posted = PostCloseToWindows(target.Pid);
                    log.Info(string.Format("已向 PID {0} ({1}) 的 {2} 个窗口发送关闭请求，最多等待 {3} 秒。",
                        target.Pid, target.Name, posted, cfg.GracefulTimeoutSeconds));

                    if (posted == 0)
                    {
                        // 没有可见窗口（后台进程/服务型），礼貌请求没有意义，直接强杀
                        log.Info(string.Format("PID {0} 没有可见窗口，改为直接结束。", target.Pid));
                        graceful = false;
                    }
                    else if (WaitForExit(p, cfg.GracefulTimeoutSeconds, shouldAbort))
                    {
                        FinishDescendants(descendants, log);
                        log.Info(string.Format("PID {0} ({1}) 已正常退出。", target.Pid, target.Name));
                        return true;
                    }
                    else
                    {
                        log.Warn(string.Format("PID {0} ({1}) 在 {2} 秒内未响应关闭请求，改为强制结束。",
                            target.Pid, target.Name, cfg.GracefulTimeoutSeconds));
                    }
                }

                // 强制结束
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    log.Error(string.Format("强制结束 PID {0} ({1}) 失败: {2}", target.Pid, target.Name, ex.Message), ex);
                    return false;
                }

                FinishDescendants(descendants, log);
                return true;
            }
            catch (Exception ex)
            {
                log.Error(string.Format("关闭 PID {0} ({1}) 时出错: {2}", target.Pid, target.Name, ex.Message), ex);
                return false;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        /// <summary>收尾：清掉残留的子进程（父进程死了但子进程还挂着的常见情况）。</summary>
        private static void FinishDescendants(List<int> pids, Logger log)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using (Process c = Process.GetProcessById(pid))
                    {
                        if (c.HasExited) continue;
                        c.Kill();
                        log.Info(string.Format("已结束残留子进程 PID {0} ({1})。", pid, c.ProcessName));
                    }
                }
                catch { }
            }
        }

        /// <summary>分片等待目标退出，期间保持对“暂停/退出”指令的响应。</summary>
        private static bool WaitForExit(Process p, int timeoutSeconds, Func<bool> shouldAbort)
        {
            int waited = 0;
            int total = timeoutSeconds * 1000;
            while (waited < total)
            {
                try { if (p.HasExited) return true; }
                catch { return true; }

                if (shouldAbort != null && shouldAbort()) return false;   // 被叫停，交给上层决定

                int slice = Math.Min(250, total - waited);
                Thread.Sleep(slice);
                waited += slice;
            }

            try { return p.HasExited; }
            catch { return true; }
        }
    }
}
