using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace IdleAppCloser
{
    /// <summary>
    /// 主设置窗口 —— 这整个程序对用户来说基本就是这一个界面。
    /// 目标是把“配置一个后台工具”压缩成三步：填程序名 → 选时长 → 点开始。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly AppHost _host;

        private TextBox _txtTarget;
        private Label _lblMatchHint;
        private NumericUpDown _numValue;
        private ComboBox _cboUnit;
        private Label _lblIdleHint;
        private CheckBox _chkAutoStart;
        private Label _lblStatus;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnLogs;
        private Timer _timer;

        private int _lastUnitIndex;
        private bool _loading;

        // 单位下拉的两项：分钟 / 秒。默认用分钟，因为“多久没操作”通常是分钟级的事。
        private const int UnitMinute = 0;
        private const int UnitSecond = 1;

        public SettingsForm(AppHost host)
        {
            _host = host;

            BuildUi();
            LoadFromConfig();
            RefreshStatus();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshStatus(); };
            _timer.Start();
        }

        // ------------------------------------------------------------------ 界面

        private void BuildUi()
        {
            // 关掉 WinForms 自带的缩放，改由本方法末尾按 DPI 手动整体缩放一次。
            //
            // 为什么不用 AutoScaleMode.Dpi：它在"纯代码建界面 + 运行时改字体"这种组合下
            // 实测根本不生效（设了之后控件尺寸依然纹丝不动，150% 缩放的机器上照样切字）。
            // 手动缩放行为完全可预测，也能直接量出来验证。
            AutoScaleMode = AutoScaleMode.None;

            Text = "空闲自动关闭程序";
            ClientSize = new Size(480, 452);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Program.AppIcon;

            try
            {
                // 有雅黑就用雅黑，老系统上没有这个字体时 Font 会自动回退，不会出错
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch { }

            Color hintColor = Color.FromArgb(120, 120, 120);

            // ---- ① 要关闭哪个程序 ----
            GroupBox grpTarget = new GroupBox();
            grpTarget.Text = "① 要关闭哪个程序";
            grpTarget.Location = new Point(12, 10);
            grpTarget.Size = new Size(456, 90);
            Controls.Add(grpTarget);

            Label lblTargetHint = new Label();
            lblTargetHint.Text = "填程序名即可，例如 chrome.exe 或 chrome（多个用英文逗号分开）";
            lblTargetHint.ForeColor = hintColor;
            lblTargetHint.Location = new Point(12, 18);
            lblTargetHint.Size = new Size(432, 18);
            grpTarget.Controls.Add(lblTargetHint);

            _txtTarget = new TextBox();
            _txtTarget.Location = new Point(12, 38);
            _txtTarget.Size = new Size(432, 24);
            _txtTarget.TextChanged += delegate { UpdateMatchHint(); };
            grpTarget.Controls.Add(_txtTarget);

            _lblMatchHint = new Label();
            _lblMatchHint.Location = new Point(12, 66);
            _lblMatchHint.Size = new Size(432, 18);
            _lblMatchHint.ForeColor = hintColor;
            grpTarget.Controls.Add(_lblMatchHint);

            // ---- ② 多久没操作 ----
            GroupBox grpIdle = new GroupBox();
            grpIdle.Text = "② 多久没有操作，就关掉它";
            grpIdle.Location = new Point(12, 108);
            grpIdle.Size = new Size(456, 88);
            Controls.Add(grpIdle);

            Label lblIdleHint = new Label();
            lblIdleHint.Text = "超过这段时间没有键盘鼠标操作，程序就会被自动关闭";
            lblIdleHint.ForeColor = hintColor;
            lblIdleHint.Location = new Point(12, 18);
            lblIdleHint.Size = new Size(432, 18);
            grpIdle.Controls.Add(lblIdleHint);

            _numValue = new NumericUpDown();
            _numValue.Location = new Point(12, 42);
            _numValue.Size = new Size(90, 24);
            _numValue.TextAlign = HorizontalAlignment.Center;
            _numValue.ValueChanged += delegate { UpdateIdleHint(); };
            grpIdle.Controls.Add(_numValue);

            _cboUnit = new ComboBox();
            _cboUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboUnit.Location = new Point(110, 42);
            _cboUnit.Size = new Size(90, 24);
            _cboUnit.Items.Add("分钟");
            _cboUnit.Items.Add("秒");
            _cboUnit.SelectedIndexChanged += OnUnitChanged;
            grpIdle.Controls.Add(_cboUnit);

            _lblIdleHint = new Label();
            _lblIdleHint.Location = new Point(210, 46);
            _lblIdleHint.Size = new Size(234, 18);
            _lblIdleHint.ForeColor = hintColor;
            grpIdle.Controls.Add(_lblIdleHint);

            // ---- ③ 开机自启 ----
            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动（不显示窗口，在后台默默运行）";
            _chkAutoStart.Location = new Point(16, 204);
            _chkAutoStart.Size = new Size(440, 22);
            Controls.Add(_chkAutoStart);

            Label lblAutoHint = new Label();
            lblAutoHint.Text = "勾选后写入当前用户的启动项，不需要管理员权限，也不用装任何东西";
            lblAutoHint.ForeColor = hintColor;
            lblAutoHint.Location = new Point(36, 226);
            lblAutoHint.Size = new Size(430, 18);
            Controls.Add(lblAutoHint);

            // ---- 运行状态 ----
            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = "运行状态";
            grpStatus.Location = new Point(12, 252);
            grpStatus.Size = new Size(456, 112);
            Controls.Add(grpStatus);

            _lblStatus = new Label();
            _lblStatus.Location = new Point(12, 20);
            _lblStatus.Size = new Size(432, 86);
            grpStatus.Controls.Add(_lblStatus);

            // ---- 按钮 ----
            _btnStart = new Button();
            _btnStart.Text = "保存并开始监控";
            _btnStart.Location = new Point(12, 376);
            _btnStart.Size = new Size(200, 32);
            _btnStart.Click += OnStartClick;
            Controls.Add(_btnStart);

            _btnStop = new Button();
            _btnStop.Text = "停止";
            _btnStop.Location = new Point(220, 376);
            _btnStop.Size = new Size(110, 32);
            _btnStop.Click += OnStopClick;
            Controls.Add(_btnStop);

            _btnLogs = new Button();
            _btnLogs.Text = "打开日志";
            _btnLogs.Location = new Point(338, 376);
            _btnLogs.Size = new Size(130, 32);
            _btnLogs.Click += OnLogsClick;
            Controls.Add(_btnLogs);

            Label lblFooter = new Label();
            lblFooter.Text = "设置和日志都保存在程序所在目录，整个文件夹删掉就等于卸载干净";
            lblFooter.ForeColor = hintColor;
            lblFooter.Location = new Point(12, 418);
            lblFooter.Size = new Size(456, 18);
            Controls.Add(lblFooter);

            // 界面全部建好之后，最后统一按屏幕实际 DPI 缩放一次。
            // 96 DPI（100% 缩放）→ 1.0 倍，原样不变；
            // 144 DPI（150% 缩放）→ 1.5 倍，容器和位置一起变大，文字就不会再被上下切掉一半。
            //
            // 上面所有坐标尺寸都按 96 DPI 的设计值写，换算只发生在这一个地方。
            float scale = GetDpiScale();
            if (scale > 1.001f)
            {
                Scale(new SizeF(scale, scale));
            }
        }

        /// <summary>
        /// 当前屏幕 DPI 相对 96 的倍数：100% 缩放返回 1.0，150% 缩放返回 1.5。
        /// 本程序在 manifest 里声明了 dpiAware，所以这里拿到的是屏幕的真实 DPI。
        /// </summary>
        private float GetDpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    return s <= 0f ? 1f : s;
                }
            }
            catch
            {
                // 拿不到 DPI 时按 100% 处理，界面大不了小一点，总好过整个打不开
                return 1f;
            }
        }

        // ------------------------------------------------------------------ 数据绑定

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                AppConfig cfg = _host.Config;

                _txtTarget.Text = string.Join(", ", cfg.TargetNames.ToArray());

                // 能整除 60 的按分钟显示，否则按秒显示 —— 让用户看到的是他填进去的那个数
                if (cfg.IdleSeconds >= 60 && (cfg.IdleSeconds % 60) == 0)
                {
                    _lastUnitIndex = UnitMinute;
                    _cboUnit.SelectedIndex = UnitMinute;
                    ApplyUnitRange(UnitMinute);
                    _numValue.Value = ClampValue(cfg.IdleSeconds / 60);
                }
                else
                {
                    _lastUnitIndex = UnitSecond;
                    _cboUnit.SelectedIndex = UnitSecond;
                    ApplyUnitRange(UnitSecond);
                    _numValue.Value = ClampValue(cfg.IdleSeconds);
                }

                _chkAutoStart.Checked = AutoStart.IsEnabled;
            }
            finally
            {
                _loading = false;
            }

            UpdateIdleHint();
            UpdateMatchHint();
        }

        private void ApplyUnitRange(int unit)
        {
            if (unit == UnitMinute)
            {
                _numValue.Minimum = 1;
                _numValue.Maximum = 1440;   // 一天
            }
            else
            {
                _numValue.Minimum = 5;
                _numValue.Maximum = 86400;
            }
        }

        private decimal ClampValue(int value)
        {
            if (value < _numValue.Minimum) return _numValue.Minimum;
            if (value > _numValue.Maximum) return _numValue.Maximum;
            return value;
        }

        private void OnUnitChanged(object sender, EventArgs e)
        {
            if (_loading) { _lastUnitIndex = _cboUnit.SelectedIndex; return; }

            int newUnit = _cboUnit.SelectedIndex;
            if (newUnit < 0 || newUnit == _lastUnitIndex) return;

            // 切换单位时把已填的数值换算过去。
            // 否则用户填了“30 分钟”再一切换成秒，会莫名其妙变成 30 秒。
            int seconds;
            if (_lastUnitIndex == UnitMinute) seconds = (int)_numValue.Value * 60;
            else seconds = (int)_numValue.Value;

            _lastUnitIndex = newUnit;
            ApplyUnitRange(newUnit);

            if (newUnit == UnitMinute) _numValue.Value = ClampValue(seconds / 60);
            else _numValue.Value = ClampValue(seconds);

            UpdateIdleHint();
        }

        /// <summary>把界面上的“数值 + 单位”换算成秒。</summary>
        private int GetIdleSeconds()
        {
            int value = (int)_numValue.Value;
            return _cboUnit.SelectedIndex == UnitMinute ? value * 60 : value;
        }

        // ------------------------------------------------------------------ 实时提示

        private void UpdateIdleHint()
        {
            if (_lblIdleHint == null) return;

            int seconds = GetIdleSeconds();
            string friendly;
            if (seconds < 60) friendly = seconds + " 秒";
            else if (seconds % 60 == 0) friendly = (seconds / 60) + " 分钟";
            else friendly = (seconds / 60) + " 分 " + (seconds % 60) + " 秒";

            _lblIdleHint.Text = "= " + friendly + "（" + seconds + " 秒）";
        }

        /// <summary>
        /// 实时告诉用户“这个名字到底找不找得到程序”—— 名字写错是这类工具最常见的失败原因，
        /// 与其让他等半小时才发现没生效，不如现在就说明白。
        /// </summary>
        private void UpdateMatchHint()
        {
            if (_lblMatchHint == null) return;

            string text = _txtTarget.Text.Trim();
            if (text.Length == 0)
            {
                _lblMatchHint.ForeColor = Color.FromArgb(120, 120, 120);
                _lblMatchHint.Text = "还没填写程序名";
                return;
            }

            try
            {
                AppConfig probe = _host.Config.Clone();
                probe.TargetNames = AppConfig.SplitTargets(text);

                if (probe.TargetNames.Count == 0)
                {
                    _lblMatchHint.ForeColor = Color.FromArgb(120, 120, 120);
                    _lblMatchHint.Text = "还没填写程序名";
                    return;
                }

                string matchMode;
                var found = ProcessCloser.FindTargets(probe, _host.Log, out matchMode);

                if (found.Count > 0)
                {
                    string names = string.Empty;
                    for (int i = 0; i < found.Count && i < 3; i++)
                    {
                        if (i > 0) names += "、";
                        names += found[i].Name + " (" + found[i].Pid + ")";
                    }
                    if (found.Count > 3) names += " 等 " + found.Count + " 个";

                    _lblMatchHint.ForeColor = Color.FromArgb(0, 130, 60);
                    _lblMatchHint.Text = "✔ 找到了：" + names;
                }
                else
                {
                    _lblMatchHint.ForeColor = Color.FromArgb(190, 90, 0);
                    _lblMatchHint.Text = "✘ 现在没找到这个程序在运行（名字拼写对吗？或者它还没启动）";
                }
            }
            catch
            {
                // 探测失败不该影响用户操作，安静地什么都不显示
                _lblMatchHint.Text = string.Empty;
            }
        }

        private void RefreshStatus()
        {
            if (_lblStatus == null) return;

            try
            {
                StatusSnapshot st = _host.Status;

                string stateText;
                if (st == null) stateText = "未开始监控";
                else if (st.Paused) stateText = "已暂停";
                else stateText = st.StateText;

                string targetText = "—";
                if (st != null && st.TargetCount > 0)
                {
                    targetText = st.TargetCount + " 个";
                    if (!string.IsNullOrEmpty(st.TargetSummary)) targetText += "（" + st.TargetSummary + "）";
                }

                _lblStatus.Text =
                    "状态：" + stateText + "\r\n" +
                    "当前已空闲：" + FormatIdle(st.IdleSeconds) + "\r\n" +
                    "找到的目标程序：" + targetText + "\r\n" +
                    "已自动关闭：" + (st == null ? 0 : st.ClosedTotal) + " 次";

                _btnStop.Enabled = _host.IsMonitoring;
            }
            catch
            {
                // 刷新失败时保持上一次的显示
            }
        }

        private static string FormatIdle(double seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return ((int)seconds) + " 秒";

            int total = (int)seconds;
            int m = total / 60;
            int s = total % 60;
            if (m < 60) return m + " 分 " + s + " 秒";

            return (m / 60) + " 小时 " + (m % 60) + " 分";
        }

        // ------------------------------------------------------------------ 按钮

        private void OnStartClick(object sender, EventArgs e)
        {
            string raw = _txtTarget.Text.Trim();
            if (raw.Length == 0)
            {
                MessageBox.Show(this,
                    "请先填写要关闭的程序名。\r\n\r\n不知道名字的话，先打开那个程序，然后按 Ctrl+Shift+Esc 打开任务管理器，" +
                    "在“进程”里找到它，把名字填进来即可。",
                    "还差一步", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _txtTarget.Focus();
                return;
            }

            var names = AppConfig.SplitTargets(raw);
            if (names.Count == 0)
            {
                MessageBox.Show(this, "程序名不能为空。", "还差一步", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _txtTarget.Focus();
                return;
            }

            int seconds = GetIdleSeconds();
            if (seconds < 1) seconds = 1;

            // 先跟用户确认一个明显不合理的情况，避免对方设完就走、回头发现程序被频繁关掉
            if (seconds < 30)
            {
                DialogResult r = MessageBox.Show(this,
                    "你设置的是 " + seconds + " 秒，这非常短 —— 只要稍微停一下不动鼠标，程序就会被关掉。\r\n\r\n确定要这样设置吗？",
                    "确认一下", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            AppConfig cfg = _host.Config;
            cfg.TargetNames = names;
            cfg.IdleSeconds = seconds;

            try
            {
                cfg.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存设置失败：" + ex.Message, "出错了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // 顺序很重要：先启动监控（主功能），再处理开机自启（附加功能）。
            // 写启动项是可能失败的（安全软件拦截、组策略限制），
            // 不能因为这一个附加功能没成，就把用户真正要的监控也拖住。
            try
            {
                _host.StartMonitoring();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动监控失败：" + ex.Message, "出错了",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                AutoStart.Refresh(Application.ExecutablePath, _chkAutoStart.Checked);
            }
            catch (Exception ex)
            {
                // 写不进去就把勾选框拨回真实状态，免得界面显示的跟实际不一致
                _chkAutoStart.Checked = AutoStart.IsEnabled;

                MessageBox.Show(this,
                    "开机自动启动没有设置成功：\r\n\r\n" + ex.Message + "\r\n\r\n" +
                    "监控已经在正常运行了，只是下次开机不会自动启动。\r\n" +
                    "常见原因是安全软件拦截了启动项的写入，把它加入信任列表后再试一次即可。",
                    "开机启动没设置成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshStatus();
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            _host.StopMonitoring();
            RefreshStatus();
        }

        private void OnLogsClick(object sender, EventArgs e)
        {
            try
            {
                string dir = _host.Log.Folder;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打不开日志目录：" + ex.Message, "出错了",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------ 关闭行为

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 正在监控时点 × ：只是把窗口收起来，后台继续工作。
            // 否则用户会以为“关掉窗口就不监控了”，而实际上他想要它一直跑着。
            if (e.CloseReason == CloseReason.UserClosing && _host.IsMonitoring)
            {
                e.Cancel = true;
                Hide();
                _host.NotifyMinimizedToTray();
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            base.Dispose(disposing);
        }
    }
}
