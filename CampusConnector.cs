using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: System.Reflection.AssemblyTitle("河科大校园网助手")]
[assembly: System.Reflection.AssemblyFileVersion("3.5.0.0")]

sealed class CampusConnector : Form
{
    const int NetworkChangeDelay = 3000, RecoveryInterval = 15000, IdleInterval = 60000;
    static readonly Color Ink = Color.FromArgb(23, 39, 53);
    static readonly Color Muted = Color.FromArgb(77, 93, 108);
    static readonly Color Accent = Color.FromArgb(17, 91, 157);
    static readonly Color Green = Color.FromArgb(23, 98, 72);
    static readonly Color Amber = Color.FromArgb(130, 75, 15);
    static readonly Color Edge = Color.FromArgb(209, 219, 230);
    readonly ComboBox profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button refresh = new Button(), toggle = new Button(), login = new Button(), browser = new Button(), account = new Button();
    readonly CheckBox startup = new CheckBox(), autoPortal = new CheckBox(), autoLogin = new CheckBox();
    readonly CheckBox startMinimized = new CheckBox();
    readonly CheckBox hideOnClose = new CheckBox();
    readonly Label headline = new Label(), detail = new Label(), accountState = new Label();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem trayToggle = new ToolStripMenuItem();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly AppSettings config;
    readonly bool startMinimizedOnLogin;
    bool exitRequested;
    bool monitoring, busy, portalOpened, updating, editing, savedAccount;
    bool steadyConnection, checkSoon, networkSubscribed;
    int networkNotificationQueued;
    int revision, missed, delay = 30;
    DateTime nextRetry, nextAuth, nextBrowserCheck;
    CancellationTokenSource activeCheck;
    AuthState? lastAuth;
    enum CheckMode { Monitor, Login, Browser }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { Checks.Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        try { return Run(args.Contains("--startup")); }
        catch (Exception error)
        {
            var saved = SaveCrashReport(error);
            MessageBox.Show("校园网助手遇到错误，已停止运行。可重新打开程序。" +
                (saved == null ? "" : "\r\n诊断文件位于 " + saved),
                "河科大校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int Run(bool background)
    {
        bool first;
        using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CampusConnector.Show"))
        using (var mutex = new Mutex(true, @"Local\CampusConnector", out first))
        {
            if (!first) { if (!background) activation.Set(); return 0; }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new CampusConnector(background))
            {
                var handle = form.Handle;
                form.BeginInvoke(new Action(form.Start));
                var wait = ThreadPool.RegisterWaitForSingleObject(activation, (state, timeout) =>
                {
                    try { form.BeginInvoke(new Action(form.Restore)); }
                    catch (InvalidOperationException) { }
                }, null, Timeout.Infinite, false);
                try
                {
                    using (var context = new ApplicationContext(form))
                        Application.Run(context);
                }
                finally { wait.Unregister(null); }
            }
        }
        return 0;
    }

    static string SaveCrashReport(Exception error)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusConnector");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "last-error.txt");
            File.WriteAllText(path, DateTime.UtcNow.ToString("O") + Environment.NewLine +
                Application.ProductVersion + Environment.NewLine + error.GetType().FullName + Environment.NewLine + error.StackTrace);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }
    }

    CampusConnector(bool background)
    {
        string warning = null;
        try { config = AppSettings.Load(); }
        catch (IOException) { config = new AppSettings(); warning = "无法读取上次设置，请重新选择校园 Wi-Fi。"; }
        catch (UnauthorizedAccessException) { config = new AppSettings(); warning = "无法读取上次设置，请检查 Windows 权限。"; }
        catch (SecurityException) { config = new AppSettings(); warning = "无法读取上次设置，请检查 Windows 权限。"; }
        startMinimizedOnLogin = background && config.StartMinimized;

        SuspendLayout();
        Text = "河科大校园网助手";
        ClientSize = new Size(620, 520);
        MinimumSize = new Size(636, 559);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 248, 251);
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 11);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 1, RowCount = 12
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 58, 86, 34, 40, 26, 50, 29, 29, 29, 29, 31 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon != null) Icon = appIcon;
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 49));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var headerImage = Icon.ToBitmap();
        header.Controls.Add(new PictureBox
        {
            Image = headerImage, Size = new Size(38, 38), SizeMode = PictureBoxSizeMode.Zoom,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6)
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = Text, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 20, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 6)
        }, 1, 0);
        layout.Controls.Add(header, 0, 0);
        Disposed += (s, e) =>
        {
            headerImage.Dispose();
            if (appIcon != null) appIcon.Dispose();
        };

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(15, 9, 15, 9),
            BackColor = Color.White, Margin = Padding.Empty
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headline.Dock = detail.Dock = DockStyle.Fill;
        headline.Margin = detail.Margin = Padding.Empty;
        headline.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        detail.ForeColor = Muted;
        card.Controls.Add(headline, 0, 0);
        card.Controls.Add(detail, 0, 1);
        layout.Controls.Add(card, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "校园 Wi-Fi", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 5)
        }, 0, 2);

        var picker = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        profiles.Dock = DockStyle.Top;
        profiles.Margin = new Padding(0, 2, 8, 0);
        picker.Controls.Add(profiles, 0, 0);
        SetButton(refresh, "刷新列表", false, (s, e) => UpdateProfiles());
        refresh.Dock = DockStyle.Top;
        picker.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(picker, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = "找不到校园网？先在 Windows 连接一次，再刷新。",
            AutoSize = true, ForeColor = Muted, Margin = Padding.Empty
        }, 0, 4);

        var accountRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
            Margin = new Padding(0, 4, 0, 5), Padding = new Padding(0, 2, 0, 0) };
        accountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        accountRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        accountState.Dock = DockStyle.Fill;
        accountState.TextAlign = ContentAlignment.MiddleLeft;
        accountState.Margin = Padding.Empty;
        accountState.Font = new Font(Font, FontStyle.Bold);
        accountRow.Controls.Add(accountState, 0, 0);
        SetButton(account, "账号设置", false, (s, e) => EditAccount());
        account.Dock = DockStyle.Top;
        accountRow.Controls.Add(account, 1, 0);
        layout.Controls.Add(accountRow, 0, 5);

        startup.Text = "登录 Windows 时自动启动";
        startMinimized.Text = "开机自启时保持最小化";
        autoLogin.Text = "使用已保存账号自动登录";
        autoPortal.Text = "未启用自动登录时打开认证页";
        hideOnClose.Text = "关闭窗口时隐藏到托盘";
        foreach (var option in new[] { startup, startMinimized, autoLogin, autoPortal, hideOnClose })
        {
            option.AutoSize = true;
            option.Margin = Padding.Empty;
            option.Anchor = AnchorStyles.Left;
        }
        layout.Controls.Add(startup, 0, 6);
        layout.Controls.Add(startMinimized, 0, 7);
        layout.Controls.Add(autoLogin, 0, 8);
        layout.Controls.Add(autoPortal, 0, 9);
        layout.Controls.Add(hideOnClose, 0, 10);
        hideOnClose.Checked = config.HideOnClose;
        startMinimized.Checked = config.StartMinimized;
        autoLogin.Checked = config.AutomaticLogin;
        autoPortal.Checked = config.AutoPortal;
        try { startup.Checked = AppSettings.StartsWithWindows(Application.ExecutablePath); }
        catch (Exception error)
        {
            if (!(error is SecurityException || error is UnauthorizedAccessException || error is IOException)) throw;
            startup.Enabled = false;
            warning = "无法读取 Windows 开机自启设置。";
        }
        startup.CheckedChanged += (s, e) => ChangeStartup();
        startMinimized.CheckedChanged += (s, e) => SaveOption(startMinimized, value => config.StartMinimized = value);
        hideOnClose.CheckedChanged += (s, e) => SaveOption(hideOnClose, value => config.HideOnClose = value);
        autoLogin.CheckedChanged += (s, e) =>
        {
            if (updating) return;
            SaveOption(autoLogin, value => config.AutomaticLogin = value);
            CancelCheck();
            ResetAuthentication();
            RequestCheck();
            UpdateActions();
        };
        autoPortal.CheckedChanged += (s, e) =>
        {
            SaveOption(autoPortal, value => config.AutoPortal = value);
            RequestCheck();
        };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0), WrapContents = false };
        SetButton(toggle, "开始监控", true, async (s, e) => await Toggle());
        SetButton(login, "立即登录", false, async (s, e) => await Check(CheckMode.Login));
        SetButton(browser, "浏览器登录", false, async (s, e) => await Check(CheckMode.Browser));
        var exit = new Button();
        SetButton(exit, "退出程序", false, (s, e) => ExitProgram());
        actions.Controls.AddRange(new Control[] { toggle, login, browser, exit });
        layout.Controls.Add(actions, 0, 11);
        profiles.SelectedIndexChanged += (s, e) => UpdateActions();
        timer.Tick += async (s, e) =>
        {
            timer.Stop();
            await Check(CheckMode.Monitor);
        };

        tray.Icon = Icon;
        tray.Text = Text;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开校园网助手", null, (s, e) => Restore());
        trayToggle.Click += async (s, e) => await Toggle();
        menu.Items.Add(trayToggle);
        menu.Items.Add("立即登录", null, async (s, e) => await Check(CheckMode.Login));
        menu.Items.Add("浏览器登录", null, async (s, e) => await Check(CheckMode.Browser));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出程序", null, (s, e) => ExitProgram());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => Restore();
        FormClosed += (s, e) =>
        {
            SetNetworkNotifications(false);
            CancelCheck();
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
        };

        ReadAccountStatus();
        UpdateProfiles();
        if (warning != null) Status("请检查设置", warning, Amber);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
    }

    async void Start()
    {
        tray.Visible = true;
        if (startMinimizedOnLogin && profiles.SelectedItem != null)
        {
            WindowState = FormWindowState.Minimized;
            Show();
        }
        else Restore();
        if (!config.Monitoring) return;
        SetMonitoring(true);
        await Check(CheckMode.Monitor);
    }

    static void SetButton(Button button, string caption, bool primary, EventHandler action)
    {
        button.Text = caption;
        button.Size = new Size(120, 36);
        button.Margin = new Padding(0, 0, 9, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Accent : Color.White;
        button.ForeColor = primary ? Color.White : Ink;
        button.FlatAppearance.BorderColor = primary ? Accent : Edge;
        button.Click += action;
    }

    void UpdateProfiles()
    {
        var previous = profiles.SelectedItem as WifiProfile;
        try
        {
            WifiProfile[] list;
            using (var wifi = new Wifi()) list = wifi.Profiles();
            profiles.Items.Clear();
            profiles.Items.AddRange(list);
            var pick = list.FirstOrDefault(p => previous != null && p.Name == previous.Name && p.Interface == previous.Interface)
                ?? list.FirstOrDefault(p => p.Name == config.ProfileName && p.Interface == config.Adapter)
                ?? list.FirstOrDefault(p => p.Name == config.ProfileName)
                ?? (string.IsNullOrEmpty(config.ProfileName)
                    ? (list.FirstOrDefault(p => p.Name == "Haust-STU" && p.Current)
                        ?? list.FirstOrDefault(p => p.Name == "Haust-STU"))
                    : null);
            profiles.SelectedItem = pick;
            if (list.Length == 0)
                Status("没有已保存的 Wi-Fi", "请打开 Windows 的 Wi-Fi，连接校园网一次，再点「刷新列表」。", Amber);
            else if (pick == null)
                Status("请选择校园 Wi-Fi", "在下方列表选择你平时连接的校园 Wi-Fi，再点「开始监控」。", Accent);
            else
                Status("监控未开启", "确认下方是校园 Wi-Fi，然后点「开始监控」。", Accent);
        }
        catch (Win32Exception)
        {
            Status("暂时无法读取 Wi-Fi", "请检查无线网卡和 Windows WLAN 服务，再点「刷新列表」。", Amber);
        }
        UpdateActions();
    }

    void UpdateActions()
    {
        var selected = profiles.SelectedItem as WifiProfile;
        bool isHaust = selected != null && selected.Name == "Haust-STU";
        profiles.Enabled = refresh.Enabled = !monitoring && !busy;
        toggle.Enabled = trayToggle.Enabled = monitoring || selected != null;
        toggle.Text = monitoring ? "暂停监控" : "开始监控";
        trayToggle.Text = toggle.Text;
        login.Enabled = !busy && selected != null;
        browser.Enabled = login.Enabled;
        autoLogin.Enabled = isHaust && savedAccount;
        autoPortal.Enabled = isHaust && (!savedAccount || !autoLogin.Checked);
    }

    void ReadAccountStatus()
    {
        try
        {
            savedAccount = AccountVault.Exists();
            accountState.Text = savedAccount ? "账号已保存 · Windows 加密" : "账号未保存";
        }
        catch (Exception error)
        {
            if (!AccountDialog.StorageProblem(error)) throw;
            savedAccount = false;
            accountState.Text = "无法读取账号设置";
        }
        UpdateActions();
    }

    async void EditAccount()
    {
        editing = true;
        CancelCheck();
        DialogResult result;
        using (var dialog = new AccountDialog()) result = dialog.ShowDialog(this);
        editing = false;
        if (result == DialogResult.OK)
        {
            ReadAccountStatus();
            ResetAuthentication();
            Status(savedAccount ? "校园网账号已保存" : "已清除校园网账号",
                savedAccount ? "点「立即登录」检测，或开启监控后自动登录。" : "仍可以使用「浏览器登录」。", Accent);
        }
        if (monitoring) await Check(CheckMode.Monitor);
    }

    void SaveOption(CheckBox option, Action<bool> set)
    {
        if (updating) return;
        bool enabled = option.Checked;
        set(enabled);
        if (SaveConfig()) return;
        set(!enabled);
        updating = true;
        option.Checked = !enabled;
        updating = false;
    }

    void Restore()
    {
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || exitRequested || !config.HideOnClose || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        tray.Visible = true;
        Hide();
    }

    void ExitProgram()
    {
        exitRequested = true;
        Close();
    }

    void Status(string title, string message, Color color)
    {
        headline.Text = title;
        headline.ForeColor = color;
        detail.Text = message;
    }

    bool SaveConfig()
    {
        try { config.Save(); return true; }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException || error is SecurityException)) throw;
            Status("设置保存失败", "请检查当前 Windows 用户的设置权限，再试一次。", Amber);
            return false;
        }
    }

    void ChangeStartup()
    {
        if (updating) return;
        try { AppSettings.SetStartup(Application.ExecutablePath, startup.Checked); }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException || error is SecurityException)) throw;
            updating = true;
            startup.Checked = !startup.Checked;
            updating = false;
            Status("开机自启修改失败", "Windows 拒绝修改启动项，请检查当前用户权限。", Amber);
        }
    }

    async Task Toggle()
    {
        if (monitoring)
        {
            config.Monitoring = false;
            if (!SaveConfig()) { config.Monitoring = true; return; }
            SetMonitoring(false);
            Status("监控已暂停", "需要时可重新开始，也可以手动打开认证页。", Accent);
            return;
        }
        var selected = profiles.SelectedItem as WifiProfile;
        if (selected == null) return;
        config.ProfileName = selected.Name;
        config.Adapter = selected.Interface;
        config.Monitoring = true;
        if (!SaveConfig()) { config.Monitoring = false; return; }
        SetMonitoring(true);
        await Check(CheckMode.Monitor);
    }

    void SetMonitoring(bool enabled)
    {
        CancelCheck();
        timer.Stop();
        monitoring = enabled;
        steadyConnection = checkSoon = false;
        SetNetworkNotifications(enabled);
        missed = 0;
        delay = 30;
        if (enabled) ResetAuthentication();
        nextRetry = DateTime.MinValue;
        if (enabled) Status("正在检测校园网", "正在确认 Wi-Fi 连接和网页认证状态。", Accent);
        UpdateActions();
    }

    void SetNetworkNotifications(bool enabled)
    {
        if (enabled && !networkSubscribed)
        {
            try
            {
                NetworkChange.NetworkAddressChanged += NetworkChanged;
                networkSubscribed = true;
            }
            catch (NetworkInformationException)
            {
                // Periodic checks also cover systems that cannot register for notifications.
            }
        }
        else if (!enabled && networkSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            networkSubscribed = false;
        }
    }

    void NetworkChanged(object sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref networkNotificationQueued, 1) != 0) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref networkNotificationQueued, 0);
                RequestCheck();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref networkNotificationQueued, 0); }
    }

    protected override void WndProc(ref Message message)
    {
        const int PowerBroadcast = 0x218, AutomaticResume = 0x12;
        if (message.Msg == PowerBroadcast && message.WParam.ToInt64() == AutomaticResume)
            RequestCheck();
        base.WndProc(ref message);
    }

    void RequestCheck()
    {
        if (!monitoring || checkSoon) return;
        checkSoon = true;
        ScheduleCheck();
    }

    void ScheduleCheck()
    {
        if (!monitoring || busy || editing || IsDisposed) return;
        timer.Stop();
        timer.Interval = checkSoon ? NetworkChangeDelay : steadyConnection ? IdleInterval : RecoveryInterval;
        timer.Start();
    }

    void CancelCheck()
    {
        revision++;
        if (activeCheck != null) activeCheck.Cancel();
    }

    void ResetAuthentication()
    {
        portalOpened = false;
        lastAuth = null;
        nextAuth = nextBrowserCheck = DateTime.MinValue;
    }

    bool IsCurrent(int request, WifiProfile profile)
    {
        return !IsDisposed && revision == request && ReferenceEquals(profiles.SelectedItem, profile);
    }

    void OpenPortal(Uri address)
    {
        try
        {
            using (Process.Start(address.AbsoluteUri)) { }
            portalOpened = true;
            Status("请在浏览器完成认证", "确认是学校登录页后再输入学号和密码。", Amber);
        }
        catch (Win32Exception)
        {
            Status("无法打开浏览器", "请检查 Windows 默认浏览器，再点「浏览器登录」。", Amber);
        }
    }

    async Task Check(CheckMode mode)
    {
        if (busy || editing || (mode == CheckMode.Monitor && !monitoring)) return;
        timer.Stop();
        checkSoon = false;
        steadyConnection = false;
        if (mode == CheckMode.Monitor && profiles.SelectedItem == null) UpdateProfiles();
        var selected = profiles.SelectedItem as WifiProfile;
        if (selected == null)
        {
            ScheduleCheck();
            return;
        }
        int request = revision;
        var stop = new CancellationTokenSource();
        activeCheck = stop;
        busy = true;
        UpdateActions();
        try
        {
            if (mode != CheckMode.Monitor)
                Status("正在检查校园网", "正在确认 Wi-Fi 和认证状态。", Accent);
            bool connected = await Task.Run(() => Connected(selected));
            if (!IsCurrent(request, selected)) return;
            if (!connected && mode == CheckMode.Login)
            {
                Status("正在连接校园 Wi-Fi", selected.Name, Accent);
                await Task.Run(() => Connect(selected, stop.Token));
                for (int i = 0; i < 5 && !connected; i++)
                {
                    await Task.Delay(1000, stop.Token);
                    connected = await Task.Run(() => Connected(selected));
                }
                if (!IsCurrent(request, selected)) return;
            }
            if (!connected)
            {
                if (mode == CheckMode.Monitor)
                    await Reconnect(request, selected);
                else
                    Status("尚未连接校园 Wi-Fi", "请确认「" + selected.Name + "」在覆盖范围内，再试一次。", Amber);
                return;
            }

            missed = 0;
            delay = 30;
            nextRetry = DateTime.MinValue;
            var local = await Task.Run(() => Wifi.LocalAddresses(selected));
            var result = await Task.Run(() => PortalProbe.Check(PortalProbe.Address, local));
            if (!IsCurrent(request, selected)) return;
            if (result.State == ProbeState.Online)
            {
                steadyConnection = true;
                ResetAuthentication();
                Status("校园网已联网", selected.Name + " · 最近检测 " + DateTime.Now.ToString("HH:mm:ss"), Green);
                return;
            }

            bool canAuthenticate = savedAccount && selected.Name == "Haust-STU" &&
                (mode == CheckMode.Login || (mode == CheckMode.Monitor && autoLogin.Checked));
            if (mode == CheckMode.Browser || (mode == CheckMode.Login && !canAuthenticate))
            {
                OpenPortal(PortalProbe.Address);
                return;
            }
            if (canAuthenticate)
            {
                bool retryable = lastAuth == AuthState.Unavailable || lastAuth == AuthState.AlreadyOnline ||
                    lastAuth == AuthState.Accepted;
                if (mode == CheckMode.Login || lastAuth == null || (retryable && DateTime.UtcNow >= nextAuth))
                {
                    Status("正在自动登录", "正在验证学校门户并提交已保存的账号。", Accent);
                    var auth = await Task.Run(() =>
                    {
                        var credentials = AccountVault.Load();
                        if (credentials == null || !Connected(selected)) return AuthState.Unavailable;
                        return HaustPortal.Login(result.Target, local, Wifi.LocalMac(selected), credentials, stop.Token);
                    });
                    if (!IsCurrent(request, selected)) return;
                    lastAuth = auth;
                    nextAuth = DateTime.UtcNow.AddMinutes(auth == AuthState.Unavailable ? 1 : 3);
                    if (auth == AuthState.Accepted || auth == AuthState.AlreadyOnline)
                    {
                        await Task.Delay(2000, stop.Token);
                        var verified = await Task.Run(() => PortalProbe.Check(PortalProbe.Address, local));
                        if (!IsCurrent(request, selected)) return;
                        if (verified.State == ProbeState.Online)
                        {
                            steadyConnection = true;
                            ResetAuthentication();
                            Status("校园网已联网", selected.Name + " · 学校认证通过", Green);
                            return;
                        }
                    }
                }
                ShowAuthStatus(lastAuth.Value);
                return;
            }
            if (result.State == ProbeState.Portal && autoPortal.Checked && !portalOpened &&
                selected.Name == "Haust-STU" && DateTime.UtcNow >= nextBrowserCheck)
            {
                nextBrowserCheck = DateTime.UtcNow.AddMinutes(1);
                var trusted = await Task.Run(() => HaustPortal.VerifyBrowserPortal(result.Target, local, stop.Token));
                if (!IsCurrent(request, selected)) return;
                if (trusted && autoPortal.Checked)
                {
                    OpenPortal(HaustPortal.Candidate(result.Target));
                    return;
                }
            }
            if (result.State == ProbeState.Portal)
                Status("校园网需要认证", "可点「账号设置」启用自动登录，或点「浏览器登录」。", Amber);
            else
                Status("Wi-Fi 已连，暂时无法上网", "可点「立即登录」重试，或点「浏览器登录」检查认证。", Amber);
        }
        catch (OperationCanceledException) { }
        catch (System.Security.Cryptography.CryptographicException)
        {
            if (IsCurrent(request, selected))
            {
                lastAuth = AuthState.Uncertain;
                Status("无法解密已保存的账号", "请在「账号设置」里重新保存学号和密码。", Amber);
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (IsCurrent(request, selected))
                Status("无法读取已保存的账号", "请检查当前 Windows 用户权限，或改用浏览器登录。", Amber);
        }
        catch (SecurityException)
        {
            if (IsCurrent(request, selected))
                Status("无法读取已保存的账号", "请检查当前 Windows 用户权限，或改用浏览器登录。", Amber);
        }
        catch (IOException)
        {
            if (IsCurrent(request, selected))
                Status("暂时无法读取账号设置", "请在「账号设置」里检查后重试。", Amber);
        }
        catch (NetworkInformationException)
        {
            if (IsCurrent(request, selected))
                Status("暂时无法读取网络状态", "程序会在下一个检测周期重试。", Amber);
        }
        catch (Win32Exception error)
        {
            if (!IsCurrent(request, selected)) return;
            if (error.NativeErrorCode == 5)
                Status("Windows 限制了 Wi-Fi 状态读取", "请在「设置 → 隐私和安全性 → 位置」检查桌面应用权限。", Amber);
            else
                Status("Wi-Fi 当前不可用", "请检查无线网卡、校园网信号或 Windows WLAN 服务。", Amber);
        }
        finally
        {
            activeCheck = null;
            stop.Dispose();
            busy = false;
            if (!IsDisposed)
            {
                UpdateActions();
                ScheduleCheck();
            }
        }
    }

    void ShowAuthStatus(AuthState state)
    {
        switch (state)
        {
            case AuthState.Accepted:
            case AuthState.AlreadyOnline:
                Status("学校已确认认证", "互联网暂时不可达，稍后会继续检测。", Amber);
                break;
            case AuthState.Rejected:
                Status("学校拒绝了本次认证", "请核对学号、密码和运营商，或在浏览器查看具体原因。", Amber);
                break;
            case AuthState.InteractionRequired:
                Status("学校要求在网页继续验证", "请点「浏览器登录」，按学校网页提示操作。", Amber);
                break;
            case AuthState.Uncertain:
                Status("未能确认认证结果", "已暂停自动提交，避免重复尝试。请点「浏览器登录」检查。", Amber);
                break;
            default:
                Status("暂时无法验证校园认证入口", "可稍后重试，或点「浏览器登录」。", Amber);
                break;
        }
    }

    static bool Connected(WifiProfile profile)
    {
        using (var wifi = new Wifi()) return wifi.IsConnected(profile);
    }

    static void Connect(WifiProfile profile, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using (var wifi = new Wifi()) wifi.Connect(profile);
    }

    async Task Reconnect(int request, WifiProfile selected)
    {
        if (DateTime.UtcNow < nextRetry) return;
        var alternate = await Task.Run(() => PortalProbe.Check(PortalProbe.Address, null));
        if (!IsCurrent(request, selected)) return;
        if (alternate.State == ProbeState.Online)
        {
            steadyConnection = true;
            missed = 0;
            Status("当前通过其他网络上网", "未连接「" + selected.Name + "」。程序会继续监控。", Accent);
            return;
        }
        if (++missed < 2)
        {
            Status("校园 Wi-Fi 已断开", "正在确认断线，随后会尝试重连「" + selected.Name + "」。", Amber);
            return;
        }
        nextRetry = DateTime.UtcNow.AddSeconds(delay);
        delay = Math.Min(300, delay * 2);
        Status("正在重连校园 Wi-Fi", selected.Name + " · 如果需要网页认证，将在连上后检测。", Accent);
        var cancellation = activeCheck.Token;
        await Task.Run(() => Connect(selected, cancellation));
    }
}
