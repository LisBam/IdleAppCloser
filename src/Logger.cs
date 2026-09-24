using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace IdleAppCloser
{
    /// <summary>
    /// 极简线程安全文件日志。
    /// 特性：按天分文件、单文件大小上限、自动清理过期日志、写失败绝不影响主逻辑。
    /// </summary>
    internal sealed class Logger
    {
        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly string _prefix;
        private readonly long _maxBytes;
        private readonly int _keepDays;
        private LogLevel _level;

        /// <summary>
        /// 是否同时把日志打到标准输出。控制台版调试时很有用；
        /// 但在 --check 这类“要干净输出给脚本解析”的场景要临时关掉，避免重复。
        /// </summary>
        public bool EnableConsoleOutput = true;

        public Logger(string dir, string prefix, LogLevel level, long maxKB, int keepDays)
        {
            _dir = dir;
            _prefix = prefix;
            _level = level;
            _maxBytes = maxKB <= 0 ? 0 : maxKB * 1024L;
            _keepDays = keepDays;
            try { Directory.CreateDirectory(_dir); } catch { }
        }

        public void SetLevel(LogLevel level) { _level = level; }

        /// <summary>日志所在文件夹。（注意别把这个属性叫 Directory，那会遮蔽 System.IO.Directory。）</summary>
        public string Folder { get { return _dir; } }

        public void Debug(string msg) { Write(LogLevel.Debug, msg, null); }
        public void Info(string msg) { Write(LogLevel.Info, msg, null); }
        public void Warn(string msg) { Write(LogLevel.Warn, msg, null); }
        public void Error(string msg) { Write(LogLevel.Error, msg, null); }
        public void Error(string msg, Exception ex) { Write(LogLevel.Error, msg, ex); }

        private void Write(LogLevel level, string msg, Exception ex)
        {
            if (level < _level) return;

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [T{2}] {3}{4}",
                DateTime.Now,
                level.ToString().ToUpperInvariant(),
                Thread.CurrentThread.ManagedThreadId,
                msg,
                ex == null ? string.Empty : "  << " + ex.GetType().Name + ": " + ex.Message);

            lock (_lock)
            {
                try
                {
                    RotateIfNeeded();
                    string path = Path.Combine(_dir, _prefix + "-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(true));
                }
                catch
                {
                    // 日志写不进去也绝不能连累主流程
                }
            }

            // 同时打到标准输出，便于用控制台版手动调试（无窗口版下等于丢弃）
            if (EnableConsoleOutput && level >= LogLevel.Info)
            {
                try { Console.WriteLine(line); } catch { }
            }
        }

        /// <summary>
        /// 当前日志文件超过上限时改名归档，并清理超过保留天数的旧日志。
        /// </summary>
        private void RotateIfNeeded()
        {
            string today = _prefix + "-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
            string path = Path.Combine(_dir, today);

            if (_maxBytes > 0 && File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (fi.Length >= _maxBytes)
                {
                    string stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
                    string archived = Path.Combine(_dir, _prefix + "-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + stamp + ".log");
                    try { File.Move(path, archived); } catch { }
                }
            }

            if (_keepDays <= 0) return;

            DateTime cutoff = DateTime.Now.Date.AddDays(-_keepDays);
            try
            {
                foreach (string f in Directory.GetFiles(_dir, _prefix + "-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>读取今天（及最近）的日志尾部，供托盘菜单“查看日志”使用。</summary>
        public string Tail(int maxLines)
        {
            var sb = new StringBuilder();
            try
            {
                var files = new List<string>(Directory.GetFiles(_dir, _prefix + "-*.log"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                int start = Math.Max(0, files.Count - 3);
                var lines = new List<string>();
                for (int i = start; i < files.Count; i++)
                {
                    try { lines.AddRange(File.ReadAllLines(files[i])); } catch { }
                }
                int from = Math.Max(0, lines.Count - maxLines);
                for (int i = from; i < lines.Count; i++) sb.AppendLine(lines[i]);
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取日志失败: " + ex.Message);
            }
            return sb.ToString();
        }
    }

    internal enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Off = 4
    }
}
