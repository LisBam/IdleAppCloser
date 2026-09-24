using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace IdleAppCloser
{
    /// <summary>
    /// 程序的中枢：把设置窗口、托盘图标和后台监控线程串起来。
    ///
    /// 分成三个对象是刻意的：
    ///   · MonitorEngine 完全不知道界面的存在，可以独立工作、独立测试；
    ///   · SettingsForm 只负责收集用户意图，不碰监控逻辑；
    ///   · AppHost 负责把它们组装起来，并决定“关窗口”到底意味着隐藏还是退出。
    /// </summary>
    internal sealed class AppHost : ApplicationContext
    {
        private readonly Logger _log;
        private readonly AppConfig _cfg;

        private NotifyIcon _tray;
        private SettingsForm _form;
        private MonitorEngine _engine;
        private Timer _trayTimer;
        private ToolStripMenuItem _menuPause;
        private bool _balloonShown;

        public AppHost(AppConfig cfg, Logger log, bool silent)
        {
            _cfg = cfg;
            _log = log;

            CreateTray();

            if (silent)
            {
                // 开机自启走的就是这条路：不弹窗口，直接进托盘开始干活
                if (_cfg.TargetNames.Count > 0)
                {
                    try { StartMonitoring(); }
                    catch (Exception ex) { _log.Error("静默启动监控失败: " + ex.Message); }
                }
                else
                {
                    _log.Info("静默启动，但还没有配置目标程序，等待用户在托盘里打开设置。");
                }
            }
            else
            {
                ShowSettings();
            }

            _trayTimer = new Timer();
            _trayTimer.Interval = 1000;
            _trayTimer.Tick += delegate { UpdateTrayState(); };
            _trayTimer.Start();
        }

        // ------------------------------------------------------------------ 对外接口

        public Logger Log { get { return _log; } }

        public AppConfig Config { get { return _cfg; } }

        public bool IsMonitoring { get { return _engine != null; } }

        public StatusSnapshot Status
        {
            get
            {
                MonitorEngine e = _engine;
                return e == null ? null : e.GetStatus();
            }
        }

        public void StartMonitoring()
        {
            StopMonitoring();

            _log.Info("用户请求开始监控。");

            // 每次开始都新建一个引擎：配置可能已经变了，
            // 让引擎只服务于一份固定配置，逻辑上简单得多，也不用去实现“热重载”。
            _engine = new MonitorEngine(_cfg, _log, false);
            _engine.Start();

            UpdateTrayState();
        }

        public void StopMonitoring()
        {
            MonitorEngine e = _engine;
            if (e == null) return;

            _engine = null;
            try { e.Stop(); }
            catch (Exception ex) { _log.Warn("停止监控时出错: " + ex.Message); }

            _log.Info("监控已停止。");
            UpdateTrayState();
        }

        public void ShowSettings()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm(this);
                _form.FormClosed += delegate
                {
                    _form = null;
                    // 没在监控时关掉窗口 = 用户想退出，别在托盘里留个幽灵图标
                    if (!IsMonitoring) ExitApp();
                };
            }

            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;

            _form.Activate();
            _form.BringToFront();
        }

        public void NotifyMinimizedToTray()
        {
            if (_balloonShown || _tray == null) return;
            _balloonShown = true;   // 只提示一次，反复弹会很烦人

            try
            {
                _tray.BalloonTipTitle = "已经缩到这儿了，监控还在继续";
                _tray.BalloonTipText = "想彻底退出，请右键这个图标选“退出”。";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
            catch { }
        }

        public void ExitApp()
        {
            _log.Info("程序退出。");

            StopMonitoring();

            if (_trayTimer != null)
            {
                _trayTimer.Stop();
                _trayTimer.Dispose();
                _trayTimer = null;
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            ExitThread();
        }

        // ------------------------------------------------------------------ 托盘

        private void CreateTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = Program.AppIcon;
            _tray.Text = "空闲自动关闭程序";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowSettings(); };

            ContextMenuStrip menu = new ContextMenuStrip();

            menu.Items.Add("打开设置", null, delegate { ShowSettings(); });

            _menuPause = new ToolStripMenuItem("暂停监控", null, OnTogglePause);
            menu.Items.Add(_menuPause);

            menu.Items.Add("打开日志", null, OnOpenLogs);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });

            _tray.ContextMenuStrip = menu;
            UpdateTrayState();
        }

        private void OnTogglePause(object sender, EventArgs e)
        {
            MonitorEngine engine = _engine;
            if (engine == null) return;

            bool nowPaused = !engine.IsPaused;
            engine.SetPaused(nowPaused);
            _log.Info(nowPaused ? "用户暂停了监控。" : "用户恢复了监控。");

            UpdateTrayState();
            if (_form != null && !_form.IsDisposed) _form.Refresh();
        }

        private void OnOpenLogs(object sender, EventArgs e)
        {
            try
            {
                string dir = _log.Folder;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                _log.Warn("打开日志目录失败: " + ex.Message);
            }
        }

        private void UpdateTrayState()
        {
            NotifyIcon tray = _tray;
            if (tray == null) return;

            MonitorEngine engine = _engine;
            StatusSnapshot st = engine == null ? null : engine.GetStatus();

            string state;
            if (engine == null) state = "未运行";
            else if (st != null && st.Paused) state = "已暂停";
            else state = "监控中";

            // 托盘提示文字上限 63 个字符，超了托盘会直接不显示，必须截断
            string text = "空闲自动关闭程序 —— " + state;
            if (engine != null && st != null && !st.Paused)
            {
                text += "，已空闲 " + FormatIdle(st.IdleSeconds);
                if (st.TargetCount > 0) text += "，目标 " + st.TargetCount + " 个";
            }
            if (text.Length > 63) text = text.Substring(0, 63);

            try { tray.Text = text; }
            catch { }

            if (_menuPause != null)
            {
                _menuPause.Enabled = engine != null;
                _menuPause.Text = (engine != null && engine.IsPaused) ? "恢复监控" : "暂停监控";
            }
        }

        private static string FormatIdle(double seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return ((int)seconds) + " 秒";

            int total = (int)seconds;
            int m = total / 60;
            if (m < 60) return m + " 分 " + (total % 60) + " 秒";

            return (m / 60) + " 小时 " + (m % 60) + " 分";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_trayTimer != null) { _trayTimer.Dispose(); _trayTimer = null; }
                if (_tray != null) { _tray.Dispose(); _tray = null; }
            }
            base.Dispose(disposing);
        }
    }
}
