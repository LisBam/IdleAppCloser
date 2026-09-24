using System;
using System.IO;
using Microsoft.Win32;

namespace IdleAppCloser
{
    /// <summary>
    /// 开机自启的实现：写当前用户的 Run 注册表项。
    ///
    /// 为什么用 Run 键而不是计划任务：
    ///   · 不需要管理员权限、不弹 UAC —— 对方双击就能用，这是“发给别人用”的第一要求；
    ///   · 卸载就是删一个值，干净利落；
    ///   · 用户可以在“任务管理器 → 启动”里看到并手动禁用它，可控、不流氓。
    ///
    /// 代价是没有“以最高权限运行”。如果目标程序本身是以管理员身份运行的，
    /// 普通权限关不掉它 —— 这种情况程序会在日志里写明原因。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>注册表里的值名，同时用于识别“是不是我们这个程序注册的”。</summary>
        private const string ValueName = "IdleAppCloser";

        /// <summary>开机自启时附加的命令行参数，让程序直接缩到托盘、不显示窗口。</summary>
        public const string SilentArgument = "--silent";

        /// <summary>当前是否已经设置了开机自启。</summary>
        public static bool IsEnabled
        {
            get
            {
                string cmd = CurrentCommand;
                return !string.IsNullOrEmpty(cmd);
            }
        }

        /// <summary>读出已登记的启动命令；没有则返回空字符串。</summary>
        public static string CurrentCommand
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    {
                        if (key == null) return string.Empty;
                        object value = key.GetValue(ValueName);
                        if (value == null) return string.Empty;
                        return value.ToString().Trim();
                    }
                }
                catch
                {
                    // 读注册表失败（极少见）时当作“没开启”，不要让界面因此崩掉
                    return string.Empty;
                }
            }
        }

        /// <summary>开启开机自启，指向当前 exe。</summary>
        public static void Enable(string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new InvalidOperationException("无法打开注册表的启动项，可能被安全软件拦截了。");

                // 路径一定要用引号包起来：万一程序放在 “C:\Program Files\...” 这类
                // 带空格的目录里，不加引号会被系统当成两个参数，启动直接失败。
                string command = "\"" + exePath + "\" " + SilentArgument;
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }

        /// <summary>关闭开机自启。</summary>
        public static void Disable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null) key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
                // 删不掉也不该让程序报错；下次启动时还会再试一次
            }
        }

        /// <summary>
        /// 把开机自启调整成想要的状态。
        /// 已经开启但登记的还是旧路径（比如用户把 exe 挪了地方）时会自动纠正过来。
        /// </summary>
        public static void Refresh(string exePath, bool wanted)
        {
            if (!wanted)
            {
                Disable();
                return;
            }

            string command = "\"" + exePath + "\" " + SilentArgument;
            if (string.Equals(CurrentCommand, command, StringComparison.OrdinalIgnoreCase))
            {
                return; // 已经是对的，不用动
            }

            Enable(exePath);
        }

        /// <summary>
        /// 判断某个启动项命令是不是本程序登记的（用于程序搬家后清理旧路径）。
        /// </summary>
        public static bool IsOurCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            string file = Path.GetFileName(command);
            return file.IndexOf("IdleAppCloser", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
