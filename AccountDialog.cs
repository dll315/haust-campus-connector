using System;
using System.Drawing;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Forms;

sealed class AccountDialog : Form
{
    readonly TextBox user = new TextBox { MaxLength = 128 };
    readonly TextBox password = new TextBox { MaxLength = 256, UseSystemPasswordChar = true };
    readonly ComboBox isp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label status = new Label();
    readonly CampusAccount previous;

    public AccountDialog()
    {
        SuspendLayout();
        Text = "校园网账号";
        Font = new Font("Microsoft YaHei UI", 11);
        ForeColor = Color.FromArgb(20, 35, 52);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(502, 306);

        bool hasAccount = false;
        try { hasAccount = AccountVault.Exists(); previous = AccountVault.Load(); }
        catch (Exception error)
        {
            if (!StorageProblem(error)) throw;
            status.Text = "无法读取旧凭据，可以重新保存账号。";
        }
        isp.Items.AddRange(new object[] { "校园网（默认）", "中国移动", "中国联通", "中国电信" });
        isp.SelectedIndex = previous == null ? 0 : Array.IndexOf(CampusAccount.IspCodes, previous.Isp);
        if (previous != null) user.Text = previous.User;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 15), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 83));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 46, 37, 37, 37, 29, 36 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        Span(layout, new Label
        {
            Text = "请输入学校登录页使用的学号和密码。\r\n凭据由 Windows 加密，仅保存在当前用户下。",
            Dock = DockStyle.Fill, Margin = Padding.Empty
        }, 0);
        AddField(layout, user, "学号", 1);
        AddField(layout, password, "密码", 2);
        AddField(layout, isp, "运营商", 3);
        var hint = new Label
        {
            Text = previous != null ? "学号不变时，密码留空会沿用原密码。" : "学校登录页若选了运营商，请在此选相同的一项。",
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(65, 82, 99), Margin = Padding.Empty
        };
        Span(layout, hint, 4);
        status.Dock = DockStyle.Fill;
        status.ForeColor = Color.FromArgb(155, 63, 15);
        Span(layout, status, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        var save = new Button { Text = "保存", Width = 105, Height = 36 };
        var clear = new Button { Text = "清除凭据", Width = 115, Height = 36, Enabled = hasAccount };
        var cancel = new Button { Text = "取消", Width = 94, Height = 36, DialogResult = DialogResult.Cancel };
        save.Click += (sender, args) => Store();
        clear.Click += (sender, args) =>
        {
            try { AccountVault.Clear(); DialogResult = DialogResult.OK; }
            catch (Exception error) { if (!StorageProblem(error)) throw; status.Text = "无法清除凭据，请检查 Windows 权限。"; }
        };
        actions.Controls.AddRange(new Control[] { save, clear, cancel });
        Span(layout, actions, 6);
        AcceptButton = save;
        CancelButton = cancel;
        ActiveControl = previous == null ? (Control)user : password;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
    }

    public static bool StorageProblem(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is SecurityException ||
            error is CryptographicException || error is ArgumentException;
    }

    void Store()
    {
        string name = user.Text.Trim(), secret = password.Text;
        if (secret.Length == 0 && previous != null && previous.User == name) secret = previous.Password;
        if (name.Length == 0 || secret.Length == 0)
        {
            status.Text = "请输入学号和校园网密码。";
            return;
        }
        try
        {
            AccountVault.Save(new CampusAccount(name, secret, CampusAccount.IspCodes[isp.SelectedIndex]));
            password.Clear();
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) { if (!StorageProblem(error)) throw; status.Text = "凭据保存失败，请检查 Windows 权限。"; }
    }

    static void AddField(TableLayoutPanel panel, Control control, string caption, int row)
    {
        panel.Controls.Add(new Label { Text = caption, Anchor = AnchorStyles.Left, AutoSize = true }, 0, row);
        control.Dock = DockStyle.Top;
        panel.Controls.Add(control, 1, row);
    }

    static void Span(TableLayoutPanel panel, Control control, int row)
    {
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }
}
