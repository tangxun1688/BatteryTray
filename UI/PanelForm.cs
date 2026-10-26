using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace BatteryTray
{

/// <summary>
/// 一张设备卡片：自定义绘制（深色主题，全宽长条电量）。
/// 版式参考用户给的设计稿：左上名称+协议出处，右上大号百分比（充电带闪电），
/// 中部全宽长条电量，底部状态行。绘制逻辑在 BatteryUI.DrawDeviceCard。
/// 交互：双击重命名；右键菜单（重命名 / 设为托盘图标 / 恢复默认名称）。
/// </summary>
internal sealed class DeviceCard : Control
{
    private string _name = "";
    private string _status = "";
    private string _source = "";
    private int _level;
    private bool _offline;
    private bool _ok;
    private bool _charging;
    private bool _pinned;

    public string Key = "";
    public string DisplayName => _name;

    public DeviceCard(string name)
    {
        _name = name;
        Height = 84;
        Width = 352;
        DoubleBuffered = true;
        // 卡片四角是圆的，必须露出窗体底色，否则会出现深色方角
        BackColor = BatteryUI.FormBg;
        Margin = new Padding(0, 0, 0, 8);
        Cursor = Cursors.Default;
    }

    public void Apply(DeviceSlot slot, bool pinned)
    {
        var r = slot.Reading;
        _ok = r.Ok;
        _offline = r.Offline;
        _level = r.Level ?? 0;
        _charging = r.Charging == true;
        _name = slot.DisplayName;
        _status = r.StatusLine;
        _source = slot.TypeLabel;   // 简介行显示设备类型（Headset/Mouse/Keyboard），不显示协议出处
        _pinned = pinned;
        Key = slot.Key;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 【防乱码】同 DarkCheckBox：先铺满整个表面再画圆角卡片，
        // 防止共享双缓冲残留内容从四角/未失效区域漏出
        using (var b = new SolidBrush(BackColor))
            g.FillRectangle(b, 0, 0, Width, Height);
        BatteryUI.DrawDeviceCard(e.Graphics, new Rectangle(0, 0, Width, Height),
            _name, _source, _ok && !_offline ? _level : (int?)null, _offline, _charging, _status, _pinned);
        base.OnPaint(e);
    }
}

/// <summary>
/// 圆角卡片容器：与设备卡片同一套深色圆角视觉（CardBg 圆角 10、四角露窗体底色）。
/// 设备卡片是整块自绘，这里是承载子控件的容器——子控件 BackColor 统一给 CardBg 融合。
/// </summary>
internal sealed class RoundedCard : Panel
{
    public RoundedCard()
    {
        DoubleBuffered = true;
        BackColor = BatteryUI.FormBg;   // 圆角外露出窗体底色
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 【防乱码】同 DarkCheckBox：先铺满整个表面
        using (var b = new SolidBrush(BackColor))
            g.FillRectangle(b, 0, 0, Width, Height);
        using (var path = BatteryUI.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
        using (var b = new SolidBrush(BatteryUI.CardBg))
            g.FillPath(b, path);
        base.OnPaint(e);
    }
}

/// <summary>
/// 自绘深色复选框（替代原生 CheckBox）：原生勾选框是白色系统样式，在深色卡片里
/// 割裂突兀（用户：跟随卡片统一样式）。16x16 圆角框垂直居中，✓ 画在框内正中
/// （用户：选中框要居中展示），文字与框垂直对齐。
/// </summary>
internal sealed class DarkCheckBox : CheckBox
{
    public DarkCheckBox(string text)
    {
        Text = text;
        AutoSize = false;
        Size = new Size(96, 26);
        ForeColor = BatteryUI.TextLight;
        Cursor = Cursors.Hand;
        // 全自绘，且 OnPaint 里必须显式铺满全表面（.NET Core 3.1 共享双缓冲残留
        // 问题，见 OnPaint 注释）。不能加 SupportsTransparentBackColor——透明模拟
        // 绘制路径在此框架上行为异常；BackColor 本就是不透明 FormBg，也用不着。
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 【防乱码关键】必须先铺满整个表面。.NET Core 3.1 的 OptimizedDoubleBuffer
        // 共享缓冲在"局部失效重绘"时会把缓冲里上一个控件（按钮/设备卡片）的残留
        // 内容整块 blit 到本控件区域——实测表现为"开机自启"一行叠着"立即刷新"
        // 文字和卡片残影（2026-10-26 绿色填充定位实验实锤：OnPaint 显式全表面
        // 填充后乱码立即消失）。只依赖 OnPaintBackground 兜底不够：它只填
        // e.ClipRectangle，局部失效时缓冲其余部分是别人的残留。
        using (var b = new SolidBrush(BatteryUI.FormBg))
            g.FillRectangle(b, 0, 0, Width, Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 16x16 圆角勾选框（垂直居中）：选中=强调蓝底+白✓，未选=控件底+描边
        var box = new Rectangle(0, Height / 2 - 8, 16, 16);
        using (var path = BatteryUI.Rounded(box, 4))
        {
            if (Checked)
            {
                using (var b = new SolidBrush(BatteryUI.MenuAccent))
                    g.FillPath(b, path);
            }
            else
            {
                using (var b = new SolidBrush(BatteryUI.CtrlBg))
                    g.FillPath(b, path);
                using (var pen = new Pen(BatteryUI.BorderColor))
                    g.DrawPath(pen, path);
            }
        }
        // ✓ 画在框内正中（居中展示）
        if (Checked)
        {
            using (var pen = new Pen(Color.White, 1.8f))
            {
                var cx = box.X + box.Width / 2;
                var cy = box.Y + box.Height / 2;
                g.DrawLines(pen, new[]
                {
                    new Point(cx - 3, cy),
                    new Point(cx - 1, cy + 3),
                    new Point(cx + 4, cy - 3),
                });
            }
        }
        // 文字：与勾选框垂直居中对齐
        using (var f = new Font("Microsoft YaHei UI", 9f))
        {
            var sz = TextRenderer.MeasureText(Text, f);
            TextRenderer.DrawText(g, Text, f,
                new Point(24, Height / 2 - sz.Height / 2), ForeColor);
        }
    }
}

/// <summary>深色面板：设备卡片 + 设置组 + 底部状态栏（上次更新 / 开机自启 / 最小化）。</summary>
internal sealed class PanelForm : Form
{
    private readonly FlowLayoutPanel _flow = new FlowLayoutPanel();
    private readonly ComboBox _interval = new ComboBox();
    private readonly DarkCheckBox _autoStart = new DarkCheckBox("开机自启");
    private readonly Button _refresh = new Button();
    private readonly Button _min = new Button();
    private readonly Label _updated = new Label();
    private readonly TrayApp _app;
    private DeviceCard _dragCard;      // 正在拖拽排序的卡片
    private Point _dragStart;          // 按下位置（超阈值才起拖）
    private bool _dragging;            // DoDragDrop 进行中
    private bool _syncing;

    public PanelForm(TrayApp app)
    {
        _app = app;
        Text = "外设电量";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 474);
        Font = new Font("Segoe UI", 9f);
        BackColor = BatteryUI.FormBg;
        ForeColor = BatteryUI.TextLight;

        // ---------- 标题区 ----------
        Controls.Add(new Label
        {
            Text = "外设电量",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14),
            ForeColor = BatteryUI.TextLight,
        });
        Controls.Add(new Label
        {
            Text = "2.4G 无线外设 · 电量自动刷新 · 双击卡片重命名",
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Location = new Point(20, 42),
            ForeColor = BatteryUI.TextDim,
        });

        // ---------- 设备卡片区（卡片顺序 = 托盘顺序，按住卡片拖动调序） ----------
        _flow.Location = new Point(16, 64);
        _flow.Size = new Size(368, 276);
        _flow.FlowDirection = FlowDirection.TopDown;
        _flow.WrapContents = false;
        _flow.AutoScroll = true;
        _flow.BackColor = BatteryUI.FormBg;
        _flow.AllowDrop = true;
        // 拖悬停在别的卡片上 → 实时换位（FlowLayoutPanel 自动重排）
        _flow.DragOver += (s, e) =>
        {
            e.Effect = DragDropEffects.Move;
            var dragCard = e.Data.GetData(typeof(DeviceCard)) as DeviceCard;
            if (dragCard == null) return;
            var target = _flow.GetChildAtPoint(_flow.PointToClient(new Point(e.X, e.Y))) as DeviceCard;
            if (target == null || target == dragCard) return;
            _flow.Controls.SetChildIndex(dragCard, _flow.Controls.GetChildIndex(target));
        };
        // 松手 → 卡片顺序落库为托盘顺序
        _flow.DragDrop += (s, e) =>
            _app?.SetOrder(_flow.Controls.OfType<DeviceCard>().Select(c => c.DisplayName));
        Controls.Add(_flow);

        // ---------- 设置区（不用卡片：卡片只留给设备展示） ----------
        // 第 1 行：刷新间隔 + 下拉框 ｜ 立即刷新（右对齐）
        Controls.Add(new Label
        {
            Text = "刷新间隔", AutoSize = true, Location = new Point(18, 353),
            ForeColor = BatteryUI.TextLight,
        });
        _interval.DropDownStyle = ComboBoxStyle.DropDownList;
        _interval.DrawMode = DrawMode.OwnerDrawFixed;
        _interval.FlatStyle = FlatStyle.Flat;
        _interval.BackColor = BatteryUI.CtrlBg;
        _interval.ForeColor = BatteryUI.TextLight;
        _interval.Items.AddRange(new object[] { "30 秒", "60 秒", "2 分钟", "5 分钟", "30 分钟" });
        _interval.Location = new Point(100, 348);
        _interval.Size = new Size(112, 24);
        // 原生下拉列表是系统色，owner-draw 才能压住深色底
        _interval.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? BatteryUI.SelBg : BatteryUI.CtrlBg))
                e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _interval.Items[e.Index].ToString(), _interval.Font,
                new Point(e.Bounds.X + 5, e.Bounds.Y + 2), BatteryUI.TextLight);
        };
        _interval.SelectedIndexChanged += (s, e) =>
        {
            if (_syncing) return;
            _app?.SetInterval(_interval.SelectedIndex switch
            {
                0 => 30, 1 => 60, 2 => 120, 3 => 300, _ => 1800
            });
        };
        Controls.Add(_interval);

        // 操作按钮统一定宽、右对齐到 x=368（左列只放设置控件，与按钮间隔 50px+，缩放也不重叠）
        _refresh.Text = "立即刷新";
        _refresh.Location = new Point(280, 347);
        _refresh.Size = new Size(88, 26);
        StyleButton(_refresh);
        _refresh.Click += (s, e) => _app?.RequestRefresh();
        Controls.Add(_refresh);

        // 第 2 行：开机自启（自绘深色复选框）｜ 最小化（右对齐）
        _autoStart.Location = new Point(18, 390);
        _autoStart.BackColor = BatteryUI.FormBg;
        _autoStart.CheckedChanged += (s, e) =>
        {
            if (_syncing) return;
            _app?.SetAutoStart(_autoStart.Checked);
        };
        Controls.Add(_autoStart);

        _min.Text = "最小化";
        _min.Location = new Point(280, 389);
        _min.Size = new Size(88, 26);
        StyleButton(_min);
        _min.Click += (s, e) => WindowState = FormWindowState.Minimized;
        Controls.Add(_min);

        // ---------- 底部状态栏 ----------
        _updated.AutoSize = true;
        _updated.Font = new Font("Segoe UI", 8.5f);
        _updated.ForeColor = BatteryUI.TextDim;
        _updated.Location = new Point(18, 442);
        _updated.Text = "上次更新 --";
        Controls.Add(_updated);

        // 托盘模式下"关闭"只是隐藏窗口（复用，省得每次重建）；
        // 独立 --panel 模式下没有托盘可再打开，必须允许真正关闭，否则进程会隐身残留。
        // 注意：Application.Exit() 遇到取消关闭的窗体会整体空转（MSDN 明示），
        // 所以托盘"退出"时必须放行，否则面板开着就退不掉。
        FormClosing += (_, e) =>
        {
            if (_app == null) return;
            if (_app.IsQuitting) return;
            e.Cancel = true;
            Hide();
        };

        // 初始值以托盘真实状态为准（面板可能是托盘跑了一会儿才打开的）
        SyncSettings();
    }

    private static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = BatteryUI.CtrlBg;
        b.ForeColor = BatteryUI.TextLight;
        b.FlatAppearance.BorderColor = BatteryUI.BorderColor;
        b.FlatAppearance.MouseOverBackColor = BatteryUI.SelBg;   // 悬停提亮，扁平观感
        b.FlatAppearance.MouseDownBackColor = BatteryUI.SelBg;
    }

    /// <summary>把托盘侧的真实状态（自启/间隔）同步回面板控件，实现双向联动。</summary>
    public void SyncSettings()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)SyncSettings); } catch { } return; }
        _syncing = true;
        try
        {
            _autoStart.Checked = _app?.IsAutoStart() ?? AutoStart.IsInstalled();
            var sec = _app?.CurrentIntervalSec() ?? 60;
            _interval.SelectedIndex = sec == 30 ? 0 : sec == 60 ? 1 : sec == 120 ? 2 : sec == 300 ? 3 : 4;
        }
        finally { _syncing = false; }
    }

    // ---------------------------------------------------------- 卡片交互（重命名 / 固定）

    /// <summary>卡片右键菜单：原生菜单（浅色），固定项打勾参照系统习惯用 ★ 前缀文字。</summary>
    private void ShowCardMenu(DeviceCard card)
    {
        if (card == null || string.IsNullOrEmpty(card.Key)) return;
        var pinned = _app != null && _app.PinnedKey == card.Key;
        var custom = _app != null && _app.CustomNameOf(card.Key) != null;

        var menu = new ContextMenuStrip();
        menu.Items.Add("重命名", null, (s, e) => RenameCard(card));
        menu.Items.Add(pinned ? "★ 取消固定托盘图标" : "设为托盘图标", null, (s, e) =>
        {
            _app?.SetPinned(pinned ? "" : card.Key);
        });
        if (custom)
            menu.Items.Add("恢复默认名称", null, (s, e) => _app?.SetCustomName(card.Key, ""));
        menu.Show(Cursor.Position);
    }

    /// <summary>双击卡片：弹出重命名框（留空 = 恢复默认名）。</summary>
    private void RenameCard(DeviceCard card)
    {
        if (card == null || string.IsNullOrEmpty(card.Key)) return;
        var current = _app?.CustomNameOf(card.Key) ?? card.DisplayName;
        var input = PromptName(current);
        if (input != null) _app?.SetCustomName(card.Key, input);
    }

    /// <summary>深色重命名对话框。返回输入文本；取消返回 null（不区分空串——空串=恢复默认）。</summary>
    private string PromptName(string current)
    {
        using (var f = new Form
        {
            Text = "重命名设备",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 140),
            BackColor = BatteryUI.FormBg,
            ForeColor = BatteryUI.TextLight,
            Font = new Font("Segoe UI", 9f),
            MaximizeBox = false,
            MinimizeBox = false,
        })
        {
            f.Controls.Add(new Label
            {
                Text = "输入设备名称（留空恢复默认名称）：",
                AutoSize = true,
                Location = new Point(16, 14),
                ForeColor = BatteryUI.TextDim,
            });
            var tb = new TextBox
            {
                Text = current ?? "",
                Location = new Point(16, 42),
                Size = new Size(308, 25),
                BackColor = BatteryUI.CtrlBg,
                ForeColor = BatteryUI.TextLight,
                BorderStyle = BorderStyle.FixedSingle,
            };
            f.Controls.Add(tb);
            var ok = new Button { Text = "确定", Location = new Point(164, 88), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Location = new Point(246, 88), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            StyleButton(ok);
            StyleButton(cancel);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            tb.Focus();
            tb.SelectAll();
            return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
        }
    }

    public void ApplyReadings(List<DeviceSlot> slots)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => ApplyReadings(slots))); } catch { } return; }

        // 面板与右键菜单/悬停提示共用同一套"托盘顺序"（_order）：先按它排，
        // 保证三处显示顺序一致。此前面板首次打开（没有已有卡片）时回退到扫描
        // 原始顺序，而菜单/悬停用的是保存的 _order，导致"重新打开后顺序不一致"。
        // 仅拖拽进行中（_dragging）沿用当前视觉顺序，避免刷新把正在拖的卡片弹回。
        if (_dragging)
        {
            var visual = _flow.Controls.OfType<DeviceCard>().Select(c => c.Key).ToList();
            if (visual.Count > 0)
                slots = slots.OrderBy(s =>
                {
                    var i = visual.IndexOf(s.Key);
                    return i < 0 ? int.MaxValue : i;
                }).ToList();
        }
        else
        {
            slots = _app?.ApplyOrder(slots) ?? slots;
        }

        _flow.SuspendLayout();
        _flow.Controls.Clear();
        if (slots.Count == 0)
        {
            var empty = new Label
            {
                Text = "未发现可用设备。\n请确认设备已连接，并检查厂商驱动是否占用了 HID 接口。",
                AutoSize = false,
                Size = new Size(340, 60),
                ForeColor = BatteryUI.TextDim,
                Location = new Point(12, 10),
            };
            _flow.Controls.Add(empty);
        }
        else
        {
            var pinnedKey = _app?.PinnedKey ?? "";
            foreach (var s in slots)
            {
                var card = new DeviceCard(s.DisplayName);
                // 双击 = 重命名；右键 = 固定/重命名菜单；按住左键拖动 = 调整托盘顺序
                card.MouseDoubleClick += (s2, e2) => RenameCard(card);
                card.MouseUp += (s2, e2) =>
                {
                    if (e2.Button == MouseButtons.Right) ShowCardMenu(card);
                };
                card.MouseDown += (s2, e2) =>
                {
                    if (e2.Button == MouseButtons.Left) { _dragCard = card; _dragStart = e2.Location; }
                };
                card.MouseMove += (s2, e2) =>
                {
                    // 移动超过阈值才起拖：给双击重命名/右键菜单让路
                    if (_dragCard != card || e2.Button != MouseButtons.Left || _dragging) return;
                    if (Math.Abs(e2.X - _dragStart.X) < 10 && Math.Abs(e2.Y - _dragStart.Y) < 10) return;
                    _dragging = true;
                    try { _flow.DoDragDrop(card, DragDropEffects.Move); }
                    finally { _dragging = false; _dragCard = null; }
                };
                _flow.Controls.Add(card);
                card.Apply(s, string.Equals(s.Key, pinnedKey, StringComparison.Ordinal));
            }
        }
        _flow.ResumeLayout();

        var last = slots.Count > 0 ? slots.Max(s => s.LastUpdate) : (DateTime?)null;
        _updated.Text = last.HasValue ? "上次更新 " + last.Value.ToString("HH:mm:ss") : "上次更新 --";
    }

    // ---------------------------------------------------------- 自验辅助（--menu 模式专用）

    internal List<DeviceCard> TestCards() => _flow.Controls.OfType<DeviceCard>().ToList();

    /// <summary>反射触发卡片右键（走真实 MouseUp → ShowCardMenu 链路）。</summary>
    internal static void TestFireRightClick(DeviceCard card) =>
        Fire(card, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, card.Width / 2, card.Height / 2, 0));

    /// <summary>反射触发卡片双击（走真实 MouseDoubleClick → RenameCard 链路）。</summary>
    internal static void TestFireDoubleClick(DeviceCard card) =>
        Fire(card, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, card.Width / 2, card.Height / 2, 0));

    private static void Fire(Control c, string method, MouseEventArgs e)
    {
        var mi = typeof(Control).GetMethod(method,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        mi?.Invoke(c, new object[] { e });
    }
}

}
