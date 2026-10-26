using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BatteryTray
{

/// <summary>
/// 自验模式（--menu）：真机截图 + 控制台断言，逐条核对 5 条反馈的落地效果。
///
/// 阶段流水线（每阶段 = 一个进入动作 + 一个延时截图/断言）：
///   等扫描 → 菜单默认态 → 设备行悬停 → 原生项悬停 → 托盘悬停 tooltip →
///   点设备行固定（图标切换 / settings.json）→ 再悬停看 tooltip ★ →
///   面板默认态 → 卡片右键菜单 → 双击重命名（自动填字）→ 恢复默认名 →
///   取消固定 → 还原 settings.json 备份。
/// </summary>
internal static class SelfTest
{
    private static TrayApp _app;
    private static PanelForm _panel;
    private static DeviceCard _card0;
    private static readonly StringBuilder _log = new StringBuilder();
    private static System.Windows.Forms.Timer _tick;
    private static System.Windows.Forms.Timer _answer;
    private static DateTime _stageAt = DateTime.Now;
    private static int _stage;
    private static bool _busy;
    private static bool _answerArmed;
    private static string _answerText = "";
    private static string _outDir;
    private static string _settingsPath;
    private static byte[] _settingsBackup;
    private static bool _hadSettings;
    private static Rectangle _iconRect;
    private static readonly Point _menuOrigin = new Point(420, 260);
    private static readonly Point _parkCursor = new Point(80, 420);
    private static EnumWindowsProc _enumProc;   // 防 GC 回收委托

    private const int WaitScan = 0, MenuDefault = 1, MenuRowHover = 2, MenuNativeHover = 3,
        TrayTip = 4, Pin = 5, TipStar = 6, MenuPinned = 7, PanelDefault = 8, CardMenu = 9,
        Rename = 10, Restore = 11, Unpin = 12, Done = 13;

    // ============================================================ 入口

    internal static void Run(TrayApp app)
    {
        _app = app;
        _outDir = AppContext.BaseDirectory;
        _settingsPath = app.TestSettingsPath;
        _hadSettings = File.Exists(_settingsPath);
        if (_hadSettings) _settingsBackup = File.ReadAllBytes(_settingsPath);
        Log("自验输出目录: " + _outDir);
        Log("settings.json: " + _settingsPath + "  (备份 " + (_hadSettings ? _settingsBackup.Length + " 字节" : "无") + ")");
        Log("虚拟屏: " + SystemInformation.VirtualScreen);

        _tick = new System.Windows.Forms.Timer { Interval = 200 };
        _tick.Tick += Tick;
        _tick.Start();

        // 重命名对话框自动应答器：等对话框出现后填字并点"确定"
        _answer = new System.Windows.Forms.Timer { Interval = 200 };
        _answer.Tick += AnswerTick;
        _answer.Start();

        Go(WaitScan);
    }

    // ============================================================ 阶段机

    private static void Go(int stage)
    {
        _stage = stage;
        _stageAt = DateTime.Now;
        Log("── 阶段 " + StageName(stage));
        switch (stage)
        {
            case MenuDefault:
                _app.ShowMenuForTest(_menuOrigin);
                LogMenuBounds();
                break;

            case MenuRowHover:
            {
                var rows = DeviceRows();
                if (rows.Count > 0) { HoverAt(ItemCenter(rows[0])); Log("悬停第一台设备行"); }
                break;
            }

            case MenuNativeHover:
            {
                var nat = _app.TestMenu.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text == "立即刷新");
                if (nat != null) { HoverAt(ItemCenter(nat)); Log("悬停原生项「立即刷新」"); }
                break;
            }

            case TrayTip:
                if (TryIconRect(out _iconRect)) Log("托盘图标矩形 = " + _iconRect);
                else { Log("!! Shell_NotifyIconGetRect 失败，转入托盘扫描"); _iconRect = SweepFindIcon(); }
                if (!_iconRect.IsEmpty) HoverAt(Center(_iconRect));
                break;

            case Pin:
                ClickDeviceRow(1);   // 点第二台设备行（固定它；只有一台就点第一台）
                break;

            case TipStar:
                if (!_iconRect.IsEmpty) HoverAt(Center(_iconRect));
                break;

            case MenuPinned:
                // 固定后重开菜单：验证设备行上的 ★ 前缀和状态点外圈强调色圆环
                _app.ShowMenuForTest(_menuOrigin);
                LogMenuBounds();
                break;

            case PanelDefault:
                _panel = new PanelForm(_app);
                _app.TestAttachPanel(_panel);
                _panel.Show();
                break;

            case CardMenu:
            {
                var cards = _panel.TestCards();
                if (cards.Count > 0)
                {
                    _card0 = cards[0];
                    var c = _card0.PointToScreen(new Point(_card0.Width / 2, _card0.Height / 2));
                    HoverAt(c);
                    PanelForm.TestFireRightClick(_card0);
                    Log("右键卡片: " + _card0.DisplayName);
                }
                else Log("!! 面板没有卡片");
                break;
            }

            case Rename:
                _answerArmed = true; _answerText = "主力鼠标";
                if (_card0 != null) PanelForm.TestFireDoubleClick(_card0);   // 阻塞到自动应答关闭对话框
                Log("重命名对话框已应答: " + _answerText);
                break;

            case Restore:
                _answerArmed = true; _answerText = "";
                if (_card0 != null) PanelForm.TestFireDoubleClick(_card0);
                Log("重命名对话框已应答: (空，恢复默认)");
                break;

            case Unpin:
                ClickDeviceRow(1);
                break;

            case Done:
                Finish();
                break;
        }
    }

    private static void Tick(object s, EventArgs e)
    {
        if (_busy) return;   // 模态对话框/扫描期间屏蔽重入
        _busy = true;
        try
        {
            var el = (DateTime.Now - _stageAt).TotalMilliseconds;
            switch (_stage)
            {
                case WaitScan:
                {
                    var slots = _app.TestSlots;
                    if (slots.Count > 0)
                    {
                        Log("扫描到 " + slots.Count + " 台设备：" + string.Join(" / ",
                            slots.Select(x => x.DisplayName + "(" +
                                (x.Reading.Ok && !x.Reading.Offline ? x.Reading.Level + "%" : x.Reading.StatusText) + ")")));
                        Go(MenuDefault);
                    }
                    else if (el > 12000)
                    {
                        Log("!! 12 秒仍未扫到设备，菜单将显示空态");
                        Go(MenuDefault);
                    }
                    break;
                }
                case MenuDefault:
                    if (el >= 600)
                    {
                        Capture("st1_menu_default.png", MenuRegion());
                        Go(MenuRowHover);
                    }
                    break;

                case MenuRowHover:
                    if (el >= 700)
                    {
                        Capture("st2_menu_row_hover.png", MenuRegion());
                        Go(MenuNativeHover);
                    }
                    break;

                case MenuNativeHover:
                    if (el >= 700)
                    {
                        Capture("st3_menu_native_hover.png", MenuRegion());
                        _app.TestCloseMenu();
                        Cursor.Position = _parkCursor;
                        Go(TrayTip);
                    }
                    break;

                case TrayTip:
                    if (el >= 1500)
                    {
                        Capture("st4_tooltip.png", FullScreen());
                        LogTooltip();
                        Cursor.Position = _parkCursor;
                        Go(Pin);
                    }
                    break;

                case Pin:
                    if (el >= 900)
                    {
                        Capture("st5_tray_pinned.png", TaskbarRegion());
                        LogPin();
                        Go(TipStar);
                    }
                    break;

                case TipStar:
                    if (el >= 1500)
                    {
                        Capture("st6_tooltip_star.png", FullScreen());
                        LogTooltip();
                        Cursor.Position = _parkCursor;
                        Go(MenuPinned);
                    }
                    break;

                case MenuPinned:
                    if (el >= 700)
                    {
                        Capture("st5b_menu_pinned.png", MenuRegion());
                        _app.TestCloseMenu();
                        Cursor.Position = _parkCursor;
                        Go(PanelDefault);
                    }
                    break;

                case PanelDefault:
                    if (el >= 900)
                    {
                        Capture("st7_panel_default.png", PanelRegion());
                        Go(CardMenu);
                    }
                    break;

                case CardMenu:
                    if (el >= 600)
                    {
                        Capture("st8_panel_cardmenu.png", MenuAroundCursor());
                        // 点面板标题区收起卡片菜单（菜单外点击即关闭）
                        if (_panel != null && !_panel.IsDisposed)
                            ClickAt(new Point(_panel.Bounds.X + 220, _panel.Bounds.Y + 24));
                        Go(Rename);
                    }
                    break;

                case Rename:
                    if (el >= 1500)
                    {
                        Capture("st9_panel_renamed.png", PanelRegion());
                        LogNames();
                        Go(Restore);
                    }
                    break;

                case Restore:
                    if (el >= 1500)
                    {
                        Capture("st10_panel_restored.png", PanelRegion());
                        LogNames();
                        Go(Unpin);
                    }
                    break;

                case Unpin:
                    if (el >= 900)
                    {
                        RestoreSettings();
                        Go(Done);
                    }
                    break;
            }
        }
        finally { _busy = false; }
    }

    private static string StageName(int s)
    {
        switch (s)
        {
            case WaitScan: return "等扫描";
            case MenuDefault: return "菜单默认态";
            case MenuRowHover: return "设备行悬停";
            case MenuNativeHover: return "原生项悬停";
            case TrayTip: return "托盘悬停 tooltip";
            case Pin: return "点设备行固定";
            case TipStar: return "固定后再悬停";
            case MenuPinned: return "固定后重开菜单";
            case PanelDefault: return "面板默认态";
            case CardMenu: return "卡片右键菜单";
            case Rename: return "双击重命名";
            case Restore: return "恢复默认名";
            case Unpin: return "取消固定 + 还原设置";
            case Done: return "结束";
        }
        return "?";
    }

    // ============================================================ 动作原语

    /// <summary>设备行从菜单索引 1 开始（0 = 表头），到第一个分隔符为止。</summary>
    private static List<ToolStripItem> DeviceRows()
    {
        var rows = new List<ToolStripItem>();
        var menu = _app.TestMenu;
        if (menu == null) return rows;
        for (var i = 1; i < menu.Items.Count; i++)
        {
            var it = menu.Items[i];
            if (it is ToolStripSeparator) break;
            if (it.Enabled) rows.Add(it);
        }
        return rows;
    }

    private static void ClickDeviceRow(int prefer)
    {
        var rows = DeviceRows();
        if (rows.Count == 0) { Log("!! 没有可点的设备行"); return; }
        var idx = prefer < rows.Count ? prefer : 0;
        Log("点击设备行 #" + idx + "（共 " + rows.Count + " 行）");
        rows[idx].PerformClick();
    }

    private static Point ItemCenter(ToolStripItem it) =>
        new Point(_menuOrigin.X + it.Bounds.X + it.Bounds.Width / 2,
                  _menuOrigin.Y + it.Bounds.Y + it.Bounds.Height / 2);

    private static Point Center(Rectangle r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>先移开 30px 再落点，保证产生 hover 状态变化（重复落同一点不触发）。</summary>
    private static void HoverAt(Point p)
    {
        Cursor.Position = new Point(p.X, Math.Max(0, p.Y - 30));
        Pump(90);
        Cursor.Position = p;
    }

    private static void ClickAt(Point p)
    {
        Cursor.Position = p;
        Pump(60);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
    }

    // ============================================================ 截图

    private static void Capture(string name, Rectangle r)
    {
        try
        {
            r.Intersect(SystemInformation.VirtualScreen);
            if (r.Width <= 0 || r.Height <= 0) { Log("!! 截图区域越界: " + name); return; }
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, bmp.Size);
                var path = Path.Combine(_outDir, name);
                bmp.Save(path, ImageFormat.Png);
                Log("截图 " + name + "  region=" + r);
            }
        }
        catch (Exception ex) { Log("!! 截图失败 " + name + ": " + ex.Message); }
    }

    private static Rectangle MenuRegion() =>
        new Rectangle(_menuOrigin.X - 20, _menuOrigin.Y - 20, 560, 400);

    private static Rectangle PanelRegion() =>
        _panel != null && !_panel.IsDisposed
            ? Rectangle.Inflate(_panel.Bounds, 12, 12)
            : new Rectangle(0, 0, 1, 1);

    private static Rectangle MenuAroundCursor() =>
        new Rectangle(Cursor.Position.X - 20, Cursor.Position.Y - 16, 340, 150);

    private static Rectangle FullScreen() => SystemInformation.VirtualScreen;

    private static Rectangle TaskbarRegion()
    {
        var tb = TaskbarRect();
        var vs = SystemInformation.VirtualScreen;
        var top = Math.Max(vs.Top, tb.Top - 240);
        var bottom = Math.Min(vs.Bottom, tb.Bottom + 10);
        return new Rectangle(vs.Left, top, vs.Width, bottom - top);
    }

    private static Rectangle TaskbarRect()
    {
        var h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero)
            return new Rectangle(0, SystemInformation.VirtualScreen.Height - 40,
                                 SystemInformation.VirtualScreen.Width, 40);
        GetWindowRect(h, out RECT r);
        return new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    // ============================================================ 托盘图标定位

    private static bool TryIconRect(out Rectangle r)
    {
        r = Rectangle.Empty;
        var h = NotifyIconWindowHandle(_app.TestNotifyIcon);
        Log("NotifyIcon 内部窗口句柄 = 0x" + h.ToString("X"));
        if (h == IntPtr.Zero) return false;
        for (uint id = 0; id <= 3; id++)
        {
            var nid = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                hWnd = h,
                uID = id,
                guidItem = Guid.Empty,
            };
            RECT rc;
            var hr = Shell_NotifyIconGetRect(ref nid, out rc);
            if (hr == 0 && rc.right > rc.left && rc.bottom > rc.top)
            {
                r = new Rectangle(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
                Log("  Shell_NotifyIconGetRect(uID=" + id + ") 命中");
                return true;
            }
        }
        Log("  Shell_NotifyIconGetRect 所有 uID 均失败");
        return false;
    }

    /// <summary>反射取 NotifyIcon 内部 NativeWindow 的句柄（字段名随 .NET 版本可能不同，逐个试）。</summary>
    private static IntPtr NotifyIconWindowHandle(NotifyIcon icon)
    {
        if (icon == null) return IntPtr.Zero;
        foreach (var name in new[] { "window", "_window" })
        {
            var f = typeof(NotifyIcon).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            var w = f?.GetValue(icon);
            if (w == null) continue;
            var p = w.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p?.GetValue(w) is IntPtr h && h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>兜底：沿任务栏从左往右扫，悬停到出现 tooltip 窗口即定位成功。</summary>
    private static Rectangle SweepFindIcon()
    {
        var tb = TaskbarRect();
        var y = tb.Top + tb.Height / 2;
        for (var x = tb.Right - 24; x > tb.Right - 560; x -= 12)
        {
            HoverAt(new Point(x, y));
            Pump(420);
            var tip = FindTooltip(new Rectangle(x - 12, y - 12, 24, 24));
            if (!tip.IsEmpty)
            {
                Log("扫描定位命中 x=" + x + "，tooltip 矩形 " + tip);
                return new Rectangle(x - 9, y - 9, 18, 18);
            }
        }
        Log("!! 扫描定位失败（任务栏被遮挡？）");
        return Rectangle.Empty;
    }

    /// <summary>
    /// 找托盘 tooltip 窗口。注意：tooltip 控件的文本存在 TOOLINFO 里而不是窗口文本，
    /// GetWindowText 拿到的是空串——所以按"类名 + 可见 + 位于托盘图标附近"判定。
    /// GetClassName 返回注册类名 "tooltips_class32"（不是原子串 "#32774"）。
    /// </summary>
    private static Rectangle FindTooltip(Rectangle nearIcon)
    {
        Rectangle found = Rectangle.Empty;
        _enumProc = (h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(128);
            GetClassName(h, cls, 128);
            var c = cls.ToString();
            if (c != "tooltips_class32" && c != "#32774") return true;
            GetWindowRect(h, out RECT r);
            var rc = new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top);
            // 只要在托盘图标附近（含上方 300px，tooltip 弹在图标上方）就算命中
            var zone = Rectangle.Inflate(nearIcon, 300, 400);
            zone.Y -= 300;
            if (zone.IntersectsWith(rc) && (found.IsEmpty || rc.Width * rc.Height > found.Width * found.Height))
                found = rc;
            return true;
        };
        EnumWindows(_enumProc, IntPtr.Zero);
        return found;
    }

    // ============================================================ 自动应答（重命名对话框）

    private static void AnswerTick(object s, EventArgs e)
    {
        if (!_answerArmed) return;
        Form dlg = null;
        try { dlg = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "重命名设备"); }
        catch { return; }
        if (dlg == null) return;
        try
        {
            var tb = dlg.Controls.OfType<TextBox>().FirstOrDefault();
            if (tb != null) tb.Text = _answerText;
            var ok = dlg.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "确定");
            if (ok != null) ok.PerformClick();
            else Log("!! 对话框里没找到「确定」按钮");
        }
        catch (Exception ex) { Log("!! 自动应答异常: " + ex.Message); }
        _answerArmed = false;
    }

    // ============================================================ 断言日志

    private static void LogMenuBounds()
    {
        var menu = _app.TestMenu;
        if (menu == null) return;
        Log(string.Format("菜单 Size={0} Bounds=({1},{2},{3},{4})",
            menu.Size, menu.Bounds.X, menu.Bounds.Y, menu.Bounds.Width, menu.Bounds.Height));
        foreach (ToolStripItem it in menu.Items)
            Log(string.Format("  [{0}] \"{1}\" bounds=({2},{3},{4},{5})",
                it.GetType().Name, it.Text, it.Bounds.X, it.Bounds.Y, it.Bounds.Width, it.Bounds.Height));
    }

    private static void LogPin()
    {
        Log("固定 Key = [" + _app.TestPinnedKey + "]");
        Log("当前 tooltip 文本:\n" + _app.TestTipText());
        Log("settings.json:\n" + _app.SettingsJson);
    }

    private static void LogNames()
    {
        Log("当前 tooltip 文本:\n" + _app.TestTipText());
        var cards = _panel != null && !_panel.IsDisposed ? _panel.TestCards() : new List<DeviceCard>();
        Log("面板卡片: " + string.Join(" / ", cards.Select(c =>
            c.DisplayName + (string.Equals(_app.PinnedKey, c.Key, StringComparison.Ordinal) ? "(★固定)" : ""))));
        Log("settings.json:\n" + _app.SettingsJson);
    }

    private static void LogTooltip()
    {
        var tip = FindTooltip(_iconRect);
        Log(tip.IsEmpty
            ? "!! 托盘附近未发现可见 tooltip 窗口（图标被遮挡或悬停未触发）"
            : "tooltip 窗口矩形 = " + tip + "（截图里目视核对内容）");
    }

    private static void RestoreSettings()
    {
        try
        {
            if (_hadSettings)
            {
                File.WriteAllBytes(_settingsPath, _settingsBackup);
                Log("settings.json 已还原为测试前内容（" + _settingsBackup.Length + " 字节）");
            }
            else if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
                Log("settings.json 原本不存在，已删除");
            }
        }
        catch (Exception ex) { Log("!! 还原 settings.json 失败: " + ex.Message); }
    }

    private static void Finish()
    {
        Log("=== 自验结束，1.2 秒后退出 ===");
        var q = new System.Windows.Forms.Timer { Interval = 1200 };
        q.Tick += (s, e) => { q.Stop(); Environment.Exit(0); };   // 自验进程直接强杀，不做收尾
        q.Start();
    }

    private static void Log(string s)
    {
        _log.Append(s).Append('\n');
        Console.WriteLine(s);
    }

    // ============================================================ Win32

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}

}
