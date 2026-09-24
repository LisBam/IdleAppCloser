using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace IdleAppCloser
{
    internal sealed class StatusSnapshot
    {
        public string StateText = "启动中";
        public bool Paused;
        public double IdleSeconds;
        public bool IsLocked;
        public bool Calibrated;
        public int TargetCount;
        public string TargetSummary = "(无)";
        public string ProtectedSummary = string.Empty;
        public int ClosedTotal;
        public string LastAction = string.Empty;
        public bool DryRun;
    }

    /// <summary>
    /// 后台监控线程：轮询空闲时间 → 命中条件 → 关闭目标进程。
    /// 所有可能阻塞的操作都在这个线程上，托盘 UI 线程不会被卡住。
    /// </summary>
    internal sealed class MonitorEngine
    {
        private readonly Logger _log;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _statusLock = new object();
        private readonly Dictionary<int, DateTime> _observedFirstSeen = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastFailedAttempt = new Dictionary<int, DateTime>();

        private const int FailedRetryCooldownSeconds = 30;

        private AppConfig _cfg;
        private IdleMonitor _idle;
        private Thread _thread;
        private volatile bool _stopRequested;
        private volatile bool _paused;
        private volatile bool _dryRun;
        private StatusSnapshot _status = new StatusSnapshot();
        private int _closedTotal;

        // 用于“只在状态变化时写日志”，防止每秒刷屏
        private string _lastLoggedSkipReason = string.Empty;
        private int _lastLoggedTargetCount = -1;

        public MonitorEngine(AppConfig cfg, Logger log, bool dryRun)
        {
            _cfg = cfg;
            _log = log;
            _dryRun = dryRun;
            _idle = new IdleMonitor(cfg.RequireActivityBeforeFirstClose);
            _status.DryRun = dryRun;

            // 把这轮生效的配置写进日志：事后排查“当时到底用的什么设置”全靠它
            _log.Info(string.Format("配置：目标={0}，空闲阈值={1} 秒，关闭方式={2}，进程最短存活={3} 秒。",
                cfg.TargetNames.Count > 0 ? string.Join(",", cfg.TargetNames.ToArray()) : string.Join(";", cfg.TargetPaths.ToArray()),
                cfg.IdleSeconds, cfg.CloseMode, cfg.EffectiveMinRunSeconds));

            foreach (string w in cfg.Warnings) _log.Warn("配置提醒: " + w);
        }

        public AppConfig Config { get { return _cfg; } }

        public bool IsPaused { get { return _paused; } }

        public StatusSnapshot GetStatus()
        {
            lock (_statusLock)
            {
                // 返回副本，避免调用方读到半更新的对象
                return new StatusSnapshot
                {
                    StateText = _status.StateText,
                    IdleSeconds = _status.IdleSeconds,
                    IsLocked = _status.IsLocked,
                    Calibrated = _status.Calibrated,
                    TargetCount = _status.TargetCount,
                    TargetSummary = _status.TargetSummary,
                    ProtectedSummary = _status.ProtectedSummary,
                    ClosedTotal = _status.ClosedTotal,
                    LastAction = _status.LastAction,
                    DryRun = _status.DryRun
                };
            }
        }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "IdleAppCloser.Monitor";
            _thread.Start();
        }

        public void Stop()
        {
            _stopRequested = true;
            _wake.Set();
            try
            {
                if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(10)))
                    _log.Warn("监控线程未在 10 秒内退出，进程将直接结束它。");
            }
            catch { }
        }

        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
            lock (_statusLock) { _status.Paused = paused; }
            _log.Info(paused ? "监控已暂停。" : "监控已恢复。");
            _wake.Set();
        }

        /// <summary>托盘菜单“立即关闭”：不等空闲超时，立刻关掉当前所有目标进程。</summary>
        public void CloseTargetsNow()
        {
            try
            {
                string mode;
                var targets = ProcessCloser.FindTargets(_cfg, _log, out mode);
                if (targets.Count == 0)
                {
                    _log.Info("手动关闭：当前没有发现目标进程。");
                    SetLastAction("手动关闭：没有目标进程");
                    return;
                }

                _log.Info(string.Format("手动关闭：开始关闭 {0} 个目标进程。", targets.Count));
                foreach (var t in targets)
                {
                    if (_dryRun)
                    {
                        _log.Info(string.Format("[试运行] 本应（手动）关闭 PID {0} ({1})。", t.Pid, t.Name));
                        continue;
                    }
                    if (ProcessCloser.Terminate(t, _cfg, _log, null))
                    {
                        Interlocked.Increment(ref _closedTotal);
                        SetLastAction(string.Format("手动关闭 {0}", t.Name));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("手动关闭出错: " + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------ 主循环

        private void Loop()
        {
            _log.Info("========================================================");
            _log.Info(string.Format("监控启动。模式={0}，空闲阈值={1} 秒，检测间隔={2} 毫秒。",
                _dryRun ? "试运行(不实际关闭)" : "正式运行", _cfg.IdleSeconds, _cfg.PollIntervalMs));
            _log.Info("目标进程: " + (_cfg.TargetNames.Count > 0
                ? string.Join(", ", _cfg.TargetNames.ToArray())
                : (_cfg.TargetPaths.Count > 0 ? string.Join("; ", _cfg.TargetPaths.ToArray()) : "(未配置)")));

            foreach (string w in _cfg.Warnings) _log.Warn("配置提醒: " + w);

            if (_cfg.TargetNames.Count == 0 && _cfg.TargetPaths.Count == 0)
                _log.Error("没有配置目标进程，监控将空转。请编辑配置文件里的 TargetProcesses。");

            while (!_stopRequested)
            {
                int interval = _cfg.PollIntervalMs;
                try
                {
                    interval = Tick();
                }
                catch (Exception ex)
                {
                    _log.Error("监控循环发生未预期错误: " + ex.Message, ex);
                    interval = Math.Max(1000, _cfg.PollIntervalMs);
                }

                if (_stopRequested) break;
                _wake.WaitOne(interval);
            }

            _log.Info("监控已停止。");
        }

        /// <summary>执行一轮检测，返回本轮结束后应等待的毫秒数。</summary>
        private int Tick()
        {
            var cfg = _cfg;

            if (_paused)
            {
                SetStatusText("已暂停");
                return cfg.PollIntervalMs;
            }

            cfg = _cfg;
            var idle = _idle.Poll(cfg.IdleSeconds);

            string matchMode;
            var targets = ProcessCloser.FindTargets(cfg, _log, out matchMode);
            var protectedRunning = ProcessCloser.FindRunningProtected(cfg);

            // ---- 更新对外状态 ----
            lock (_statusLock)
            {
                _status.StateText = _dryRun ? "试运行中" : "运行中";
                _status.IdleSeconds = idle.IdleSeconds;
                _status.IsLocked = idle.IsLocked;
                _status.Calibrated = idle.IsTrustworthy;
                _status.TargetCount = targets.Count;
                _status.TargetSummary = targets.Count == 0
                    ? "(无)"
                    : string.Join(", ", targets.ConvertAll(t => t.Name + "(" + t.Pid + ")").ToArray());
                _status.ProtectedSummary = protectedRunning.Count == 0
                    ? string.Empty
                    : string.Join(", ", protectedRunning.ToArray());
                _status.ClosedTotal = _closedTotal;
            }

            // 周期性心跳（Debug 级别）：排查“为什么一直不关”时最有用的就是这一条，
            // 它把监控器眼里的空闲秒数、阈值、目标数量直接摆出来，不用靠猜。
            _tickCount++;
            if ((_tickCount % 5) == 0)
            {
                _log.Debug(string.Format(
                    "心跳：已空闲 {0:F1} 秒（阈值 {1} 秒，进程最短存活 {2} 秒）{3}；目标进程 {4} 个{5}",
                    idle.IdleSeconds, cfg.IdleSeconds, cfg.EffectiveMinRunSeconds,
                    idle.IsLocked ? "，已锁屏（按配置计入空闲）" : string.Empty,
                    targets.Count,
                    protectedRunning.Count > 0 ? "，保护进程 " + protectedRunning.Count + " 个" : string.Empty));
            }

            // 目标数量变化时记一条日志
            if (targets.Count != _lastLoggedTargetCount)
            {
                _lastLoggedTargetCount = targets.Count;
                if (targets.Count > 0)
                    _log.Info(string.Format("检测到 {0} 个目标进程（{1}）: {2}", targets.Count, matchMode,
                        string.Join(", ", targets.ConvertAll(t => t.Name + "#" + t.Pid).ToArray())));
                else
                    _log.Debug("当前没有目标进程在运行。");
            }

            // ---- 安全阀 1：空闲检测尚未校准 ----
            if (!idle.IsTrustworthy)
            {
                LogOnce(ref _lastLoggedSkipReason, "uncalibrated",
                    string.Format("空闲检测尚未校准（启动时系统已空闲 {0:F0} 秒）；为安全起见，等到观测到一次用户操作后才开始关闭。", idle.IdleSeconds));
                return cfg.PollIntervalMs;
            }

            // ---- 安全阀 2：保护进程在运行 ----
            if (protectedRunning.Count > 0)
            {
                LogOnce(ref _lastLoggedSkipReason, "protected:" + string.Join(",", protectedRunning.ToArray()),
                    "保护进程正在运行（" + string.Join(", ", protectedRunning.ToArray()) + "），本轮不关闭任何目标。");
                return cfg.PollIntervalMs;
            }

            // ---- 计算有效空闲时间 ----
            double idleEffective = idle.IdleSeconds;
            if (cfg.LockedCountsAsIdle && idle.IsLocked && idleEffective < cfg.IdleSeconds)
            {
                // 锁屏本身就算“没在操作”，不必再干等 X 秒
                idleEffective = cfg.IdleSeconds;
                _log.Debug("检测到锁屏，按已空闲处理。");
            }

            if (idleEffective < cfg.IdleSeconds)
            {
                _lastLoggedSkipReason = string.Empty;
                return cfg.PollIntervalMs;
            }

            if (targets.Count == 0) return cfg.PollIntervalMs;

            // ---- 命中条件，逐个处理 ----
            _log.Info(string.Format("空闲 {0:F0} 秒 ≥ 阈值 {1} 秒{2}，开始检查 {3} 个目标进程。",
                idleEffective, cfg.IdleSeconds, idle.IsLocked ? "（锁屏）" : string.Empty, targets.Count));

            var currentPids = new HashSet<int>();
            foreach (var t in targets) currentPids.Add(t.Pid);

            foreach (var t in targets)
            {
                if (_stopRequested) break;

                // 进程最少存活时间：防止刚打开就被关掉
                double lifetime = GetLifetimeSeconds(t);
                if (lifetime >= 0 && lifetime < cfg.EffectiveMinRunSeconds)
                {
                    _log.Info(string.Format("PID {0} ({1}) 才运行 {2:F0} 秒（要求至少 {3} 秒），本轮跳过。",
                        t.Pid, t.Name, lifetime, cfg.EffectiveMinRunSeconds));
                    continue;
                }

                // 全屏保护
                if (cfg.SkipWhenFullScreen && ProcessCloser.HasFullscreenWindow(t.Pid))
                {
                    _log.Info(string.Format("PID {0} ({1}) 正占据全屏窗口，本轮跳过。", t.Pid, t.Name));
                    continue;
                }

                // 失败冷却，避免每秒重试刷屏
                DateTime lastFail;
                if (_lastFailedAttempt.TryGetValue(t.Pid, out lastFail) &&
                    (DateTime.Now - lastFail).TotalSeconds < FailedRetryCooldownSeconds)
                {
                    continue;
                }

                if (_dryRun)
                {
                    _log.Info(string.Format("[试运行] 本应关闭 PID {0} ({1})：空闲 {2:F0} 秒，已运行 {3}。路径={4}",
                        t.Pid, t.Name, idleEffective,
                        lifetime >= 0 ? string.Format("{0:F0} 秒", lifetime) : "未知",
                        string.IsNullOrEmpty(t.Path) ? "(取不到)" : t.Path));
                    SetLastAction(string.Format("[试运行] {0}({1})", t.Name, t.Pid));
                    continue;
                }

                _log.Info(string.Format("关闭目标 PID {0} ({1})：空闲 {2:F0} 秒，已运行 {3}。",
                    t.Pid, t.Name, idleEffective,
                    lifetime >= 0 ? string.Format("{0:F0} 秒", lifetime) : "未知"));

                bool ok = ProcessCloser.Terminate(t, cfg, _log, delegate { return _stopRequested; });
                if (ok)
                {
                    Interlocked.Increment(ref _closedTotal);
                    _lastFailedAttempt.Remove(t.Pid);
                    SetLastAction(string.Format("已关闭 {0}({1})，空闲 {2:F0} 秒", t.Name, t.Pid, idleEffective));
                }
                else
                {
                    _lastFailedAttempt[t.Pid] = DateTime.Now;
                    _log.Warn(string.Format("PID {0} ({1}) 关闭失败，{2} 秒内不再重试。常见原因：权限不足（可把计划任务设为“最高权限运行”）。",
                        t.Pid, t.Name, FailedRetryCooldownSeconds));
                    SetLastAction(string.Format("关闭 {0}({1}) 失败", t.Name, t.Pid));
                }
            }

            // 清理已消失进程的记录
            var stale = new List<int>();
            foreach (var kv in _observedFirstSeen) if (!currentPids.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (int pid in stale) _observedFirstSeen.Remove(pid);

            return cfg.PollIntervalMs;
        }

        /// <summary>
        /// 取进程已运行秒数。优先用系统给出的真实启动时间；
        /// 权限不足取不到时，退化为“本程序第一次看到它的时间”。
        /// </summary>
        private double GetLifetimeSeconds(TargetProcess t)
        {
            if (t.StartTimeKnown) return t.LifetimeSeconds;

            DateTime first;
            if (!_observedFirstSeen.TryGetValue(t.Pid, out first))
            {
                first = DateTime.Now;
                _observedFirstSeen[t.Pid] = first;
            }
            return (DateTime.Now - first).TotalSeconds;
        }

        private void LogOnce(ref string slot, string key, string message)
        {
            if (slot == key) return;
            slot = key;
            _log.Info(message);
        }

        private int _tickCount;

        private void SetStatusText(string text)
        {
            lock (_statusLock) { _status.StateText = text; }
        }

        private void SetLastAction(string text)
        {
            lock (_statusLock) { _status.LastAction = DateTime.Now.ToString("HH:mm:ss") + " " + text; }
        }
    }
}
