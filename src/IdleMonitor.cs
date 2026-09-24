using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IdleAppCloser
{
    internal struct IdleState
    {
        /// <summary>距最后一次用户输入（键鼠）经过的秒数。</summary>
        public double IdleSeconds;
        /// <summary>当前会话是否处于锁屏 / 屏保 / UAC 安全桌面。</summary>
        public bool IsLocked;
        /// <summary>空闲检测是否可信（首次观测到用户活动后为 true）。</summary>
        public bool IsTrustworthy;

        public override string ToString()
        {
            return string.Format("空闲 {0:F1}s{1}{2}",
                IdleSeconds,
                IsLocked ? " [已锁屏]" : string.Empty,
                IsTrustworthy ? string.Empty : " [检测未校准]");
        }
    }

    /// <summary>
    /// 基于 GetLastInputInfo 的会话级空闲检测。
    ///
    /// 关于 TickCount 回绕：GetTickCount() 与 LASTINPUTINFO.dwTime 都是 32 位无符号、
    /// 约 49.7 天回绕一次。用 (uint)(now - last) 做无符号减法，回绕时结果天然正确，
    /// 不需要任何 if 分支。原参考脚本把 int/uint 混用后手工比较大小，那个分支是错的，
    /// 会让系统连续开机 24.8 天以上时（TickCount 变负数）空闲时间算成一个巨大的值，
    /// 一边疯狂误判“空闲超时”，一边又在另一段区间永不触发。
    /// </summary>
    internal sealed class IdleMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

        private bool _hasSeenActivity;
        private bool _requireActivityFirst;

        public IdleMonitor(bool requireActivityBeforeFirstClose)
        {
            _requireActivityFirst = requireActivityBeforeFirstClose;
        }

        /// <summary>配置热重载后允许改变这个安全阀开关，同时保留已校准状态。</summary>
        public void SetRequireActivityFirst(bool value)
        {
            _requireActivityFirst = value;
        }

        /// <summary>程序启动后是否已经观测到过一次“用户确实在操作”，用于安全阀。</summary>
        public bool HasSeenActivity { get { return _hasSeenActivity; } }

        /// <summary>强制认为“已校准”（用于配置关闭安全阀时）。</summary>
        public void ForceCalibrated() { _hasSeenActivity = true; }

        public IdleState Poll(int idleThresholdSeconds)
        {
            var st = new IdleState();

            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));

            if (GetLastInputInfo(ref lii))
            {
                uint now = GetTickCount();
                uint delta = unchecked(now - lii.dwTime);   // 无符号减法，自动处理 49.7 天回绕
                st.IdleSeconds = delta / 1000.0;

                // 一旦观测到用户活动，就认为检测链路是可信的
                if (st.IdleSeconds < idleThresholdSeconds) _hasSeenActivity = true;
            }
            else
            {
                // 极罕见：取不到就当作“未知”，不参与判断
                st.IdleSeconds = 0;
            }

            st.IsLocked = IsInputDesktopLocked();
            st.IsTrustworthy = _hasSeenActivity || !_requireActivityFirst;
            return st;
        }

        /// <summary>
        /// 当前会话是否真的处于锁屏状态。
        ///
        /// 只用 OpenInputDesktop 试探是不够的：它失败既可能是“真锁屏”（输入桌面切到
        /// Winlogon 安全桌面），也可能只是“权限不足”。若把后者也当成锁屏，而配置里又开了
        /// LockedCountsAsIdle，就会导致目标程序被立刻误杀。
        /// 因此这里做交叉验证：只有 OpenInputDesktop 失败 **且** 锁屏界面进程 LogonUI 存在，
        /// 才判定为锁屏；其余情况一律保守地当作“没锁屏”（交给空闲计时去决定）。
        /// </summary>
        public static bool IsInputDesktopLocked()
        {
            if (CanOpenInputDesktop()) return false;   // 能打开输入桌面，确定没锁屏
            return IsLogonUiRunning();                 // 打不开时再用 LogonUI 交叉确认
        }

        private static bool CanOpenInputDesktop()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
                return h != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { CloseDesktop(h); } catch { }
                }
            }
        }

        /// <summary>锁屏时 Winlogon 会拉起 LogonUI.exe，用它作为锁屏的佐证。</summary>
        private static bool IsLogonUiRunning()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("LogonUI");
                try
                {
                    return procs.Length > 0;
                }
                finally
                {
                    foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                }
            }
            catch
            {
                return false;   // 查不到就认为没锁屏，绝不因此误杀
            }
        }
    }
}
