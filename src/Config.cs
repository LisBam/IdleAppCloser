using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleAppCloser
{
    internal enum CloseMode
    {
        /// <summary>先给目标主窗口发 WM_CLOSE，等它自己保存退出，超时再强杀。推荐。</summary>
        Graceful,
        /// <summary>直接强杀，不给自己保存的机会。</summary>
        Force
    }

    /// <summary>
    /// INI 风格配置。设计原则：
    ///  1. 任何一项写错都不能让程序起不来 —— 出错就用默认值并记日志；
    ///  2. 节名（[Target] 等）只是给人看的，查找键时全局搜索，放错节也能生效；
    ///  3. 支持 # 和 ; 两种行注释，以及 "值  # 行内注释"。
    /// </summary>
    internal sealed class AppConfig
    {
        // ---- 目标程序 ----
        public List<string> TargetNames = new List<string>();      // 进程名，不含 .exe，比较时不区分大小写
        public List<string> TargetPaths = new List<string>();      // 完整路径（非空时优先按路径匹配）

        // ---- 空闲判定 ----
        public int IdleSeconds = 300;              // X：连续无操作多少秒后关闭
        public int PollIntervalMs = 1000;          // 检测间隔
        public bool LockedCountsAsIdle = true;     // 锁屏立刻算作空闲（不必再等 X 秒）
        public int MinRunSeconds = -1;             // 目标进程最短存活秒数，-1 表示取 IdleSeconds

        // 安全阀：程序启动时若系统已处于空闲状态，先不关任何东西，
        // 等观测到一次真实用户操作后再开始工作。避免“监控自启时恰好人不在，
        // 结果立刻把目标程序误杀”这类事故。默认开启，强烈建议保持。
        public bool RequireActivityBeforeFirstClose = true;

        // ---- 保护规则 ----
        public List<string> ProtectedProcesses = new List<string>(); // 这些进程在跑就绝对不关
        public bool SkipWhenFullScreen = false;                      // 目标有全屏窗口时跳过（看视频/演示）

        // ---- 关闭方式 ----
        public CloseMode CloseMode = CloseMode.Graceful;
        public int GracefulTimeoutSeconds = 15;
        public bool KillChildProcesses = true;

        // ---- 日志 ----
        public LogLevel LogLevel = LogLevel.Info;
        public long LogMaxKB = 2048;
        public int LogKeepDays = 14;

        // ---- 界面 / 行为 ----
        // 说明：改成图形界面之后，托盘图标是必需的（窗口关掉后要靠它找回设置界面），
        // 配置热重载也不需要了 —— 用户点“保存并开始监控”时会直接重建监控引擎。

        // ---- 元信息 ----
        public string ConfigPath = string.Empty;
        public DateTime LoadedAt = DateTime.MinValue;
        public List<string> Warnings = new List<string>();

        /// <summary>实际生效的“进程最短存活秒数”。</summary>
        public int EffectiveMinRunSeconds
        {
            get { return MinRunSeconds < 0 ? IdleSeconds : MinRunSeconds; }
        }

        public static AppConfig Load(string path, Logger log)
        {
            var cfg = new AppConfig();
            cfg.ConfigPath = path;

            if (!File.Exists(path))
            {
                if (log != null) log.Warn("配置文件不存在，使用内置默认值: " + path);
                cfg.Warnings.Add("配置文件不存在: " + path);
                cfg.LoadedAt = DateTime.Now;
                return cfg;
            }

            Dictionary<string, string> kv;
            try
            {
                kv = ParseIni(File.ReadAllLines(path, DetectEncoding(path)));
            }
            catch (Exception ex)
            {
                if (log != null) log.Error("读取配置文件失败，使用默认值: " + ex.Message, ex);
                cfg.Warnings.Add("读取配置失败: " + ex.Message);
                cfg.LoadedAt = DateTime.Now;
                return cfg;
            }

            Func<string, string> get = delegate(string key)
            {
                string v;
                return kv.TryGetValue(key, out v) ? v.Trim() : null;
            };

            cfg.TargetNames = SplitList(get("TargetProcesses"), ',', ';', '|');
            cfg.TargetPaths = SplitList(get("TargetPaths"), ';', '|');
            cfg.ProtectedProcesses = SplitList(get("ProtectedProcesses"), ',', ';', '|');

            cfg.IdleSeconds = Int(get, "IdleSeconds", cfg.IdleSeconds, 1, 86400, cfg);
            cfg.PollIntervalMs = Int(get, "PollIntervalMs", cfg.PollIntervalMs, 100, 60000, cfg);
            cfg.LockedCountsAsIdle = Bool(get, "LockedCountsAsIdle", cfg.LockedCountsAsIdle, cfg);
            cfg.MinRunSeconds = Int(get, "MinRunSeconds", cfg.MinRunSeconds, -1, 86400, cfg);
            cfg.RequireActivityBeforeFirstClose = Bool(get, "RequireActivityBeforeFirstClose", cfg.RequireActivityBeforeFirstClose, cfg);
            cfg.SkipWhenFullScreen = Bool(get, "SkipWhenFullScreen", cfg.SkipWhenFullScreen, cfg);

            string mode = get("CloseMode");
            if (mode != null)
            {
                if (string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase)) cfg.CloseMode = CloseMode.Force;
                else if (string.Equals(mode, "graceful", StringComparison.OrdinalIgnoreCase)) cfg.CloseMode = CloseMode.Graceful;
                else cfg.Warnings.Add("CloseMode 取值无效（应为 graceful 或 force），按 graceful 处理: " + mode);
            }

            cfg.GracefulTimeoutSeconds = Int(get, "GracefulTimeoutSeconds", cfg.GracefulTimeoutSeconds, 1, 600, cfg);
            cfg.KillChildProcesses = Bool(get, "KillChildProcesses", cfg.KillChildProcesses, cfg);

            string lvl = get("LogLevel");
            if (lvl != null)
            {
                bool ok = true;
                switch (lvl.ToLowerInvariant())
                {
                    case "debug": cfg.LogLevel = LogLevel.Debug; break;
                    case "info": cfg.LogLevel = LogLevel.Info; break;
                    case "warn":
                    case "warning": cfg.LogLevel = LogLevel.Warn; break;
                    case "error": cfg.LogLevel = LogLevel.Error; break;
                    case "off":
                    case "none": cfg.LogLevel = LogLevel.Off; break;
                    default: ok = false; break;
                }
                if (!ok) cfg.Warnings.Add("LogLevel 取值无效，按 Info 处理: " + lvl);
            }

            cfg.LogMaxKB = Int(get, "LogMaxKB", (int)cfg.LogMaxKB, 0, 1024 * 1024, cfg);
            cfg.LogKeepDays = Int(get, "LogKeepDays", cfg.LogKeepDays, 0, 3650, cfg);

            cfg.LoadedAt = DateTime.Now;

            if (cfg.TargetNames.Count == 0 && cfg.TargetPaths.Count == 0)
                cfg.Warnings.Add("没有配置任何 TargetProcesses / TargetPaths，程序不会关闭任何东西。");

            return cfg;
        }

        // ---------------------------------------------------------------- 保存与克隆

        /// <summary>把用户输入的文本拆成进程名列表（界面和配置文件共用同一套规则）。</summary>
        public static List<string> SplitTargets(string value)
        {
            return SplitList(value, ',', ';', '|');
        }

        /// <summary>拷贝一份，给界面做“试算”用，不污染正在生效的配置。</summary>
        public AppConfig Clone()
        {
            AppConfig c = (AppConfig)MemberwiseClone();
            c.TargetNames = new List<string>(TargetNames);
            c.TargetPaths = new List<string>(TargetPaths);
            c.ProtectedProcesses = new List<string>(ProtectedProcesses);
            c.Warnings = new List<string>(Warnings);
            return c;
        }

        /// <summary>
        /// 保存界面上的设置。
        /// 只更新 TargetProcesses 和 IdleSeconds 两个键，文件里的其它内容原样保留 ——
        /// 这样即便用户手工加过进阶选项（比如 CloseMode=force），也不会被界面覆盖掉。
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(ConfigPath))
                throw new InvalidOperationException("没有配置文件路径，无法保存。");

            string dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var lines = new List<string>();
            if (File.Exists(ConfigPath))
            {
                try { lines.AddRange(File.ReadAllLines(ConfigPath, DetectEncoding(ConfigPath))); }
                catch { }
            }

            if (lines.Count == 0) lines.AddRange(DefaultTemplate());

            SetKey(lines, "TargetProcesses", string.Join(", ", TargetNames.ToArray()));
            SetKey(lines, "IdleSeconds", IdleSeconds.ToString(CultureInfo.InvariantCulture));

            // 带 BOM 写：用户哪天用记事本打开这个文件，中文不会变乱码
            File.WriteAllLines(ConfigPath, lines.ToArray(), new UTF8Encoding(true));

            // 配置已经变了，之前加载时攒下的告警不再适用，重新判断一遍。
            // 不清掉的话，日志里会一直挂着“没有配置任何目标程序”这种早就过期的警告 ——
            // 程序明明在正常关闭东西，这条告警却会把排查的人引到完全错误的方向。
            Warnings.Clear();
            if (TargetNames.Count == 0 && TargetPaths.Count == 0)
                Warnings.Add("没有配置任何 TargetProcesses / TargetPaths，程序不会关闭任何东西。");
        }

        private static void SetKey(List<string> lines, string key, string value)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].TrimStart();
                if (t.Length == 0 || t[0] == '#' || t[0] == ';' || t[0] == '[') continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;

                if (string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    return;
                }
            }
            lines.Add(key + "=" + value);
        }

        /// <summary>首次保存时生成的文件内容，带中文说明，方便用户日后自己查看或微调。</summary>
        private static List<string> DefaultTemplate()
        {
            return new List<string>(new string[]
            {
                "# 空闲自动关闭程序 —— 设置文件",
                "#",
                "# 平时不需要手动改它：在程序界面上填写并保存，就会自动写到这里。",
                "# 如果想调整界面上没有的选项，直接在下面加一行即可，程序会自动读取。",
                "",
                "# 要关闭的程序名（不含 .exe 也可以），多个用英文逗号分开",
                "TargetProcesses=",
                "",
                "# 连续多少秒没有键盘鼠标操作后关闭（界面上填的分钟会换算成秒存到这里）",
                "IdleSeconds=300",
                "",
                "# ---- 以下是可选的进阶选项，保持默认即可 ----",
                "# 锁屏时是否立刻算作空闲：true = 一锁屏就关，不用再等上面那么多秒",
                "LockedCountsAsIdle=true",
                "# 关闭方式：graceful = 先请程序自己关闭（有机会保存数据），超时才强制结束；force = 直接强制结束",
                "CloseMode=graceful",
                "# 只要这些程序在运行，就绝不关闭目标程序，多个用逗号分开",
                "ProtectedProcesses=",
                "# 日志详细程度：Debug / Info / Warn / Error / Off",
                "LogLevel=Info"
            });
        }

        // ---------------------------------------------------------------- 解析辅助

        private static Dictionary<string, string> ParseIni(string[] lines)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';') continue;   // 整行注释
                if (line[0] == '[') continue;                     // 节名，忽略
                if (line[0] == '\uFEFF') line = line.Substring(1).Trim(); // 去掉 BOM 残留

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                // 去掉 "值  # 注释" 形式（要求 # / ; 前至少有一个空白，避免误伤路径中的字符）
                for (int i = 0; i < val.Length; i++)
                {
                    if ((val[i] == '#' || val[i] == ';') && i > 0 && char.IsWhiteSpace(val[i - 1]))
                    {
                        val = val.Substring(0, i).Trim();
                        break;
                    }
                }

                kv[key] = val;   // 同名键后者覆盖前者
            }
            return kv;
        }

        private static Encoding DetectEncoding(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length >= 3)
                    {
                        int b0 = fs.ReadByte(), b1 = fs.ReadByte(), b2 = fs.ReadByte();
                        if (b0 == 0xEF && b1 == 0xBB && b2 == 0xBF) return new UTF8Encoding(true);
                    }
                }
            }
            catch { }
            // 默认按系统 ANSI(中文 GBK) 读：记事本另存为“ANSI”的情况很常见
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
            catch { return Encoding.UTF8; }
        }

        private static List<string> SplitList(string value, params char[] seps)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(value)) return list;
            foreach (string part in value.Split(seps))
            {
                string s = part.Trim().Trim('"');
                if (s.Length > 0 && !list.Contains(s)) list.Add(s);
            }
            return list;
        }

        private static int Int(Func<string, string> get, string key, int def, int min, int max, AppConfig cfg)
        {
            string s = get(key);
            if (string.IsNullOrEmpty(s)) return def;
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                cfg.Warnings.Add(string.Format("{0} 不是合法数字（{1}），按默认值 {2} 处理。", key, s, def));
                return def;
            }
            if (v < min || v > max)
            {
                cfg.Warnings.Add(string.Format("{0}={1} 超出范围 [{2}, {3}]，已夹取。", key, v, min, max));
                return Math.Min(max, Math.Max(min, v));
            }
            return v;
        }

        private static bool Bool(Func<string, string> get, string key, bool def, AppConfig cfg)
        {
            string s = get(key);
            if (string.IsNullOrEmpty(s)) return def;
            switch (s.ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "y": return true;
                case "0": case "false": case "no": case "off": case "n": return false;
            }
            cfg.Warnings.Add(string.Format("{0} 取值无效（{1}），按 {2} 处理。", key, s, def));
            return def;
        }

        /// <summary>给托盘“查看当前配置”用的可读摘要。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("配置文件: " + ConfigPath);
            sb.AppendLine("目标进程(名称): " + (TargetNames.Count == 0 ? "(未设置)" : string.Join(", ", TargetNames.ToArray())));
            sb.AppendLine("目标进程(路径): " + (TargetPaths.Count == 0 ? "(未设置)" : string.Join("; ", TargetPaths.ToArray())));
            sb.AppendLine("空闲阈值 X: " + IdleSeconds + " 秒");
            sb.AppendLine("进程最短存活: " + EffectiveMinRunSeconds + " 秒");
            sb.AppendLine("检测间隔: " + PollIntervalMs + " 毫秒");
            sb.AppendLine("锁屏即算空闲: " + LockedCountsAsIdle);
            sb.AppendLine("首次活动前不动作(安全阀): " + RequireActivityBeforeFirstClose);
            sb.AppendLine("全屏时跳过: " + SkipWhenFullScreen);
            sb.AppendLine("保护进程: " + (ProtectedProcesses.Count == 0 ? "(无)" : string.Join(", ", ProtectedProcesses.ToArray())));
            sb.AppendLine("关闭方式: " + (CloseMode == CloseMode.Graceful
                ? "graceful（先请求正常关闭，" + GracefulTimeoutSeconds + " 秒后强杀）"
                : "force（直接强杀）"));
            sb.AppendLine("连带结束子进程: " + KillChildProcesses);
            sb.AppendLine("日志级别: " + LogLevel + "，单文件上限 " + LogMaxKB + " KB，保留 " + LogKeepDays + " 天");
            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("配置问题:");
                foreach (string w in Warnings) sb.AppendLine("  - " + w);
            }
            return sb.ToString();
        }
    }
}
