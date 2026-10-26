using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BatteryTray
{

/// <summary>
/// 托盘常驻部分。消息循环由隐藏窗体承载 —— NotifyIcon 依附于它，
/// 退出走 Application.Exit()，从根上避免"窗口没了进程还在"的僵尸进程。
/// </summary>
internal sealed class TrayApp : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HiddenWindow _window;
    private readonly MenuRowItem _autoStartItem;
    private readonly MenuRowItem _menuHeader;
    private readonly List<ToolStripMenuItem> _deviceMenuItems = new List<ToolStripMenuItem>();
    private PanelForm _panel;

    private Icon _icon;               // 多尺寸 ICO（16+32），托盘按实际尺寸取原生分辨率
    private MemoryStream _iconStream; // Icon(Stream) 要求流在图标存活期间保持打开
    private List<DeviceSlot> _slots = new List<DeviceSlot>();
    private readonly object _gate = new object();
    private bool _busy;
    private int _intervalSec = 60;
    private bool _autoStart;
    private List<string> _order = new List<string>();
    private bool _quitting;
    private string _pinnedKey = "";   // 固定到托盘图标的设备 Key（空 = 自动选第一台有读数的）
    private readonly Dictionary<string, string> _customNames = new Dictionary<string, string>();  // Key → 自定义名

    public TrayApp()
    {
        Native.SetProcessDpiAwareness(Native.PROCESS_SYSTEM_DPI_AWARE);

        _window = new HiddenWindow();
        _iconStream = BuildIcoStream(null, false, false);
        _icon = new Icon(_iconStream);
        _notify = new NotifyIcon
        {
            Icon = _icon,
            // 悬停提示改用自绘 TipWindow（像素级三列对齐，见 TipWindow.cs），
            // 原生 tooltip 的 Text 置空不再显示——空 Text 时 shell 不弹框
            Text = "",
            Visible = true,
        };
        _notify.MouseMove += (s, e) => ShowTip();
        _notify.MouseClick += OnMouseClick;
        _notify.BalloonTipClicked += (s, e) => ShowPanel();

        var menu = new ContextMenuStrip
        {
            // 菜单钉宽 300（用户要求收窄）：设备行两列固定坐标（见 BatteryUI.DrawDeviceRow）
            MinimumSize = new Size(300, 0),
            // 始终保留最左勾选列：开机自启的 ✓ 与设备"固定点"都画在这一列，
            // 不预留系统图像列/勾选列：默认那两道空列让左侧显得又宽又空，
            // 改为自绘内容从 x=26 起（与设备行统一左对齐），✓ 在 x=10 自绘，
            // 选中态不再依赖系统列，也不会因开关勾选而左右抖动。
            ShowImageMargin = false,
            ShowCheckMargin = false,
            // 深色主题渲染器：背景/悬停/边框/分隔线全走 MenuTheme 配色表，
            // 设备行自绘内容和文本项从此一个风格（用户要求统一，参考 G HUB / WorkBuddy）
            Renderer = new DarkMenuRenderer(),
            // 中英混排统一雅黑（同悬停窗/面板卡片）
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        // 表头 + 设备行：设备行直接嵌在菜单里（自绘），每次刷新后由
        // RebuildDeviceMenu 重建；点某台设备 = 固定/取消固定到托盘图标
        // 表头与下方所有操作项统一用 MenuRowItem（与设备行同一套自绘样式），
        // 文字都左对齐到绝对 x=43（与设备名同一列），视觉统一（用户要求）。
        _menuHeader = new MenuRowItem("设备电量", style: RowStyle.Header);
        menu.Items.Add(_menuHeader);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new MenuRowItem("打开面板", (s, e) => ShowPanel()));
        menu.Items.Add(new MenuRowItem("立即刷新", (s, e) => RefreshAsync()));
        menu.Items.Add(new ToolStripSeparator());
        // 开机自启：勾选态在最左勾选列自绘浅色 ✓（深底上清晰），
        // 与设备"固定点"同一列、同一套自绘逻辑（用户要求选中态统一到左侧）。
        _autoStartItem = new MenuRowItem("开机自启", (s, e) => ToggleAutoStart(), checkable: true);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new MenuRowItem("退出", (s, e) => Quit()));
        _notify.ContextMenuStrip = menu;

        _timer = new System.Windows.Forms.Timer { Interval = _intervalSec * 1000 };
        _timer.Tick += (s, e) => RefreshAsync();

        LoadSettings();
    }

    // ---------------------------------------------------------- 生命周期

    public void Start()
    {
        _timer.Start();
        RefreshAsync();
        Application.Run(_window);
    }

    /// <summary>自验用（--menu 模式）：起定时器和首次刷新，但不进消息循环。</summary>
    internal void BeginTest()
    {
        _timer.Start();
        RefreshAsync();
    }

    private void Quit()
    {
        _timer.Stop();
        _quitting = true;   // 面板 FormClosing 见到这个标记就放行，否则 Application.Exit 会因"取消关闭"整体空转
        _notify.Visible = false;
        _notify.Dispose();
        Application.Exit();
        // 兜底：万一还有窗体取消关闭，1 秒后强杀，杜绝"托盘没了进程还在"的僵尸
        var killer = new System.Windows.Forms.Timer { Interval = 1000 };
        killer.Tick += (s, e) => { killer.Stop(); Environment.Exit(0); };
        killer.Start();
    }

    /// <summary>是否正在退出（面板据此放行 FormClosing）。</summary>
    internal bool IsQuitting => _quitting;

    // ---------------------------------------------------------- 刷新

    private void OnMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) RefreshAsync();
        else if (e.Button == MouseButtons.Right) { /* ContextMenuStrip 自动弹出 */ }
    }

    private void RefreshAsync()
    {
        lock (_gate)
        {
            if (_busy) return;
            _busy = true;
        }
        Task.Run(() =>
        {
            try { return DeviceManager.Scan(); }
            catch { return new List<DeviceSlot>(); }
            finally { }
        }).ContinueWith(t =>
        {
            List<DeviceSlot> result;
            try { result = t.Result; }
            catch { result = new List<DeviceSlot>(); }

            lock (_gate)
            {
                // 自定义名优先于协议默认名（扫描后统一套上，菜单/面板/悬停都生效）。
                // Key 为稳定硬件标识（见 Devices.Scan），改名/重插/休眠重枚举都不会失配。
                foreach (var s in result)
                {
                    string custom;
                    if (_customNames.TryGetValue(s.Key, out custom) && !string.IsNullOrEmpty(custom))
                        s.DisplayName = custom;
                }
                _slots = result;
                _busy = false;
            }
            UpdateTray();   // 内部会顺带刷面板（改名/固定后不用等下一轮扫描）
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateTray()
    {
        List<DeviceSlot> snap;
        lock (_gate) snap = _slots;

        // 悬停提示改自绘（TipWindow），原生 Text 留空；内容变化时热更新
        _tip?.UpdateRows(ApplyOrder(snap));

        // 图标只显示一台：排序里第一台有读数的设备（Choose 内部会应用排序）
        var chosen = Choose(snap);

        var oldIcon = _icon;
        var oldStream = _iconStream;
        _iconStream = BuildIcoStream(chosen?.Reading.Level, chosen?.Reading.Offline ?? true,
            chosen?.Reading.Charging == true);
        _icon = new Icon(_iconStream);
        _notify.Icon = _icon;
        oldIcon?.Dispose();
        oldStream?.Dispose();

        // 低电量通知：所有设备监控；从 20% 起每降 5% 提醒一次（18 首报 → 13 → 8 → 3），
        // 回到 20% 以上清掉记录，下次跌破重新提醒
        foreach (var s in snap)
        {
            if (s.Reading.Ok && s.Reading.Level <= 20)
            {
                var last = _lastLowLevel.TryGetValue(s.Key, out var lv) ? lv : int.MaxValue;
                if (s.Reading.Level <= last - 5 && !_quitting)
                {
                    _lastLowLevel[s.Key] = s.Reading.Level.Value;
                    _notify.ShowBalloonTip(4000, "电量偏低",
                        $"{s.DisplayName} 剩余 {s.Reading.Level}%", ToolTipIcon.Warning);
                }
            }
            else if (s.Reading.Ok && s.Reading.Level > 20)
            {
                _lastLowLevel.Remove(s.Key);
            }
        }

        RebuildDeviceMenu();
        // 面板跟着图标一起刷：改名/固定是即时操作，不能等下一轮扫描（最长一个间隔）
        _panel?.ApplyReadings(snap);
    }

    /// <summary>已提醒过的低电量水位：低于它 5% 才再提醒（用户要求每隔 5% 通知一次）。</summary>
    private readonly Dictionary<string, int> _lastLowLevel = new Dictionary<string, int>();

    private DeviceSlot Choose(List<DeviceSlot> slots)
    {
        if (slots.Count == 0) return null;
        // 用户固定了某台设备：优先显示它（哪怕离线——用户明确要盯这台）
        if (!string.IsNullOrEmpty(_pinnedKey))
        {
            var pinned = slots.FirstOrDefault(s => string.Equals(s.Key, _pinnedKey, StringComparison.Ordinal));
            if (pinned != null) return pinned;
        }
        var ordered = ApplyOrder(slots);
        return ordered.FirstOrDefault(s => s.Reading.Ok) ?? ordered[0];
    }

    internal List<DeviceSlot> ApplyOrder(List<DeviceSlot> slots)
    {
        if (_order.Count == 0) return slots;
        var byName = slots.GroupBy(s => s.DisplayName)
                          .ToDictionary(g => g.Key, g => g.First());
        var result = new List<DeviceSlot>();
        foreach (var name in _order)
        {
            if (byName.TryGetValue(name, out var s)) { result.Add(s); byName.Remove(name); continue; }
            // 顺序表里是旧名（改名前存的、或改名时设备不在线）：按协议默认名/自定义名兜底匹配，
            // 别让改了名的设备掉到队尾
            var hit = slots.FirstOrDefault(x => !result.Contains(x) &&
                (string.Equals(x.DefaultName, name, StringComparison.Ordinal) ||
                 string.Equals(CustomNameOf(x.Key) ?? "", name, StringComparison.Ordinal)));
            if (hit != null) { result.Add(hit); byName.Remove(hit.DisplayName); }
        }
        result.AddRange(byName.Values);
        return result;
    }

    // ---------------------------------------------------------- 面板

    private void ShowPanel()
    {
        try
        {
            if (_panel == null || _panel.IsDisposed)
            {
                _panel = new PanelForm(this);
                List<DeviceSlot> snap;
                lock (_gate) snap = _slots;
                _panel.ApplyReadings(snap);
            }
            // 最小化状态要先还原，否则窗口不显示
            if (_panel.WindowState == FormWindowState.Minimized)
                _panel.WindowState = FormWindowState.Normal;
            // 面板的"关闭"其实是 Hide（见 PanelForm.FormClosing），隐藏态下 Activate
            // 不会把窗口显示出来——这就是"关了面板再也打不开"的根因，必须先 Show。
            if (!_panel.Visible) _panel.Show();
            _panel.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开面板失败：\n" + ex.Message, "外设电量",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private TipWindow _tip;

    /// <summary>悬停托盘图标：弹出/跟随自绘提示（MouseMove 每次移动都会调到这里）。</summary>
    private void ShowTip()
    {
        if (_quitting) return;
        if (_tip == null || _tip.IsDisposed)
        {
            _tip = new TipWindow();
            _tip.Clicked = () => ShowPanel();   // 点提示窗 = 打开面板
        }
        List<DeviceSlot> snap;
        lock (_gate) snap = _slots;
        _tip.ShowTip(ApplyOrder(snap), Cursor.Position);
    }

    /// <summary>刷新后重建右键菜单里的设备行（菜单开着时不重建，避免闪烁）。</summary>
    private void RebuildDeviceMenu()
    {
        var menu = _notify.ContextMenuStrip;
        if (menu == null || menu.Visible) return;

        foreach (var it in _deviceMenuItems) menu.Items.Remove(it);
        _deviceMenuItems.Clear();

        List<DeviceSlot> snap;
        lock (_gate) snap = _slots;
        var ordered = ApplyOrder(snap);

        if (ordered.Count == 0)
        {
            _deviceMenuItems.Add(new MenuRowItem("未发现可用设备", style: RowStyle.Dim) { Enabled = false });
        }
        else
        {
            foreach (var s in ordered)
            {
                var item = new DeviceMenuItem(s, string.Equals(s.Key, _pinnedKey, StringComparison.Ordinal));
                // 点设备行 = 固定/取消固定到托盘图标（再点一次取消）
                item.Click += (snd, ev) => TogglePin(s.Key);
                _deviceMenuItems.Add(item);
            }
        }
        for (var i = 0; i < _deviceMenuItems.Count; i++)
            menu.Items.Insert(1 + i, _deviceMenuItems[i]);

        var last = ordered.Count > 0 ? ordered.Max(s => s.LastUpdate) : (DateTime?)null;
        _menuHeader.SetLabel(last.HasValue ? "设备电量 · 更新于 " + last.Value.ToString("HH:mm:ss") : "设备电量");
    }

    /// <summary>
    /// 菜单里的所有"行"统一走这个自绘项——设备行(DeviceMenuItem)以外的表头 / 操作项 /
    /// 占位符都用它，保证与设备行同一套视觉（全宽悬停高亮 + 文字左对齐到 x=43）。
    /// base.OnPaint 画原生选择背景（悬停高亮由 DarkMenuRenderer 渲染，与设备行一致）；
    /// 文字自绘到绝对 x=43（与设备名同一列），实现左对齐。不依赖 ToolStripMenuItem.Text
    /// （置空，避免基类在默认位置重复绘制）。
    ///   · checkable：最左勾选列自绘浅色对勾（开机自启），与设备"固定点"同一列。
    ///   · style：Normal（浅色）/ Dim（灰，占位符）/ Header（加粗灰，表头）。
    /// </summary>
    private enum RowStyle { Normal, Dim, Header }

    private sealed class MenuRowItem : ToolStripMenuItem
    {
        private string _label;
        private readonly bool _checkable;
        private readonly RowStyle _style;
        private bool _checkedState;

        public MenuRowItem(string label, EventHandler click = null,
                           bool checkable = false, RowStyle style = RowStyle.Normal)
        {
            _label = label;
            _checkable = checkable;
            _style = style;
            Text = "";                 // 自绘文字，避免基类在默认位置重复画
            AccessibleName = label;
            AutoSize = false;
            Width = 300;
            Height = 30;
            ForeColor = StyleColor(style);
            Enabled = style != RowStyle.Header;   // 表头不可点
            if (click != null) Click += click;
        }

        public bool CheckState { get => _checkedState; set => _checkedState = value; }

        public void SetLabel(string text) => _label = text;

        private static Color StyleColor(RowStyle s) =>
            s == RowStyle.Dim ? BatteryUI.MenuDim :
            s == RowStyle.Header ? Color.FromArgb(120, 120, 130) :
            BatteryUI.TextLight;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);   // 原生选择背景（悬停/按下，全宽）
            var g = e.Graphics;

            if (_style == RowStyle.Header)
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, _label, font, new Point(26, 7), Color.FromArgb(120, 120, 130));
                return;
            }

            // 最左勾选列画浅色对勾（与设备固定点同一图标、同一位置、同一配色）
            if (_checkable && _checkedState)
                BatteryUI.DrawCheck(g, new Point(13, Height / 2));

            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var font = new Font("Microsoft YaHei UI", 9.5f))
                TextRenderer.DrawText(g, _label, font, new Point(26, 7), StyleColor(_style));
        }
    }

    /// <summary>
    /// 右键菜单里的设备行。是真正的 ToolStripMenuItem（不是托管控件）：
    /// base.OnPaint 让菜单渲染器画原生选择背景（视觉样式渐变 + 强调色边缘、全宽），
    /// 颜色和宽度与"打开面板"等原生项完全一致；上面再画行内容。
    /// AutoSize=false + Width=300 让 bounds 横跨整个菜单宽（菜单 MinimumSize 同宽）：
    /// 整行可点、内容不被裁——这是早先"电量显示超出右边界"的根因。
    /// </summary>
    private sealed class DeviceMenuItem : ToolStripMenuItem
    {
        private readonly DeviceSlot _slot;
        private readonly bool _pinned;

        public DeviceMenuItem(DeviceSlot slot, bool pinned)
        {
            _slot = slot;
            _pinned = pinned;
            AutoSize = false;
            Width = 300;
            Height = 30;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);   // 原生选择背景（悬停时由渲染器画，全宽）
            var rd = _slot.Reading;
            // 内容用绝对坐标自绘（见 BatteryUI.DrawDeviceRow）：勾选列 x=10、文字 x=26、
            // 电池 x=200，与 MenuRowItem 统一左对齐。不再预留系统图像/勾选列。
            BatteryUI.DrawDeviceRow(e.Graphics, new Rectangle(0, 0, Width, Height),
                _slot.DisplayName, rd.Level, rd.Offline, rd.Charging == true, _pinned);
        }
    }

    // ---------------------------------------------------------- 固定 / 重命名

    /// <summary>点设备行：固定该设备到托盘图标；再点一次取消（回到自动选择）。</summary>
    private void TogglePin(string key)
    {
        _pinnedKey = string.Equals(_pinnedKey, key, StringComparison.Ordinal) ? "" : key;
        SaveSettings();
        UpdateTray();   // 立即换图标；菜单已关，下次打开会带上固定标记
    }

    /// <summary>固定状态 Key（面板同步用）。</summary>
    internal string PinnedKey => _pinnedKey;

    /// <summary>面板里"设为托盘图标"。</summary>
    internal void SetPinned(string key)
    {
        var k = key ?? "";
        if (_pinnedKey == k) return;
        _pinnedKey = k;
        SaveSettings();
        UpdateTray();
    }

    /// <summary>某设备的自定义名（没有返回 null）。</summary>
    internal string CustomNameOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _customNames.TryGetValue(key, out var n) ? n : null;
    }

    /// <summary>设置/清除自定义名。留空 = 恢复默认名。</summary>
    internal void SetCustomName(string key, string name)
    {
        if (string.IsNullOrEmpty(key)) return;
        var has = _customNames.ContainsKey(key);
        var trimmed = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        if (trimmed.Length == 0)
        {
            if (has) _customNames.Remove(key);
        }
        else
        {
            _customNames[key] = trimmed;
        }
        // 托盘顺序表按显示名存：改名后同步旧名（含首次自定义名），免得设备掉到队尾。
        // 同时立即回写当前槽位的显示名：否则要等下一轮扫描（最长一个间隔）菜单/悬停/面板才换名。
        lock (_gate)
        {
            foreach (var s in _slots)
            {
                if (!string.Equals(s.Key, key, StringComparison.Ordinal)) continue;
                var oldName = s.DisplayName;
                var newName = trimmed.Length > 0 ? trimmed : s.DefaultName;
                s.DisplayName = newName;
                for (var i = 0; i < _order.Count; i++)
                    if (_order[i] == oldName) _order[i] = newName;
            }
        }
        SaveSettings();
        UpdateTray();
    }

    /// <summary>托盘悬停文本：全部设备一行一个，按用户排序。
    /// tooltip 是纯文本画不了图，彩色 emoji（补充平面字符）在提示框里会渲染成
    /// 灰色乱码块，只能用 BMP 字符近似。固定中的设备加 ★ 前缀。
    /// 名称列按"显示宽度"（中日韩字符记 2、ASCII 记 1）补空格对齐，电量条
    /// 三行左对齐（用户要求）；不画充电闪电，充电详情看面板。</summary>
    private string BuildTip(List<DeviceSlot> slots)
    {
        var rows = new List<Tuple<string, string, string>>();
        foreach (var s in ApplyOrder(slots))
        {
            var r = s.Reading;
            var name = (string.Equals(s.Key, _pinnedKey, StringComparison.Ordinal) ? "★" : "") + s.DisplayName;
            // 三列：名称 ｜ 百分比（无读数显示 --）｜ 状态（超短文案）
            rows.Add(Tuple.Create(name, r.Ok ? r.Level.Value + "%" : "--", r.ShortStatus));
        }
        return FormatTip(rows);
    }

    /// <summary>tooltip 排版：名称/百分比两列都按真实像素宽补空格，三列严格左对齐
    /// （用户要求去掉电量方块条，只留 名称/电量/状态 三列）。
    /// tooltip 是比例字体（Segoe UI 9pt），字符计数法必然错位——必须用 GDI
    /// （TextRenderer，与 tooltip 同一套绘制引擎）按 NoPadding 实测像素宽再换算空格数。
    /// 第二列的补格会修正第一列的取整误差，保证累计误差 ≤ 半个空格。</summary>
    internal static string FormatTip(System.Collections.Generic.IEnumerable<Tuple<string, string, string>> rows)
    {
        var list = new List<Tuple<string, string, string>>(rows);
        if (list.Count == 0) return "未发现可用设备";

        using (var font = new Font("Segoe UI", 9f))
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding;
            var proposed = new Size(short.MaxValue, short.MaxValue);
            int W(string s) => TextRenderer.MeasureText(s, font, proposed, flags).Width;

            var namePx = list.Max(x => W(x.Item1));
            var pctPx = list.Max(x => W(x.Item2));
            var spacePx = Math.Max(1, W(" "));
            var sb = new StringBuilder();
            foreach (var row in list)
            {
                if (sb.Length > 0) sb.Append('\n');
                var w1 = W(row.Item1);
                var pad1 = Math.Max(0, (int)Math.Round((namePx - w1) / (double)spacePx));
                var prefix1 = w1 + pad1 * spacePx;
                // 第二列以"实际前缀宽"为基准补格，吃掉第一列的取整误差
                var pad2 = Math.Max(0, (int)Math.Round(
                    (namePx + 2 * spacePx + pctPx - prefix1 - 2 * spacePx - W(row.Item2)) / (double)spacePx));
                sb.Append(row.Item1).Append(' ', pad1).Append("  ")
                  .Append(row.Item2).Append(' ', pad2).Append("  ")
                  .Append(row.Item3);
            }
            return sb.ToString();
        }
    }

    internal void SetInterval(int seconds)
    {
        _intervalSec = Math.Max(30, Math.Min(1800, seconds));
        _timer.Interval = _intervalSec * 1000;
        SaveSettings();
    }

    internal void RequestRefresh() => RefreshAsync();

    /// <summary>自验用（--menu 模式）：在指定屏幕位置弹出右键菜单，供截图核对版式。</summary>
    internal void ShowMenuForTest(Point p)
    {
        var menu = _notify.ContextMenuStrip;
        if (menu == null) return;
        // 菜单开着时 RebuildDeviceMenu 会跳过，先强制重建一次保证行是最新的
        RebuildDeviceMenu();
        menu.Show(p);
    }

    // ---------------------------------------------------------- 自验访问器（--menu 模式专用）

    internal ContextMenuStrip TestMenu => _notify.ContextMenuStrip;
    internal NotifyIcon TestNotifyIcon => _notify;
    internal string TestSettingsPath => SettingsPath;
    internal string TestPinnedKey => _pinnedKey;
    internal List<DeviceSlot> TestSlots { get { lock (_gate) return _slots.ToList(); } }
    internal string TestTipText() { lock (_gate) return BuildTip(_slots); }
    internal string SettingsJson { get { try { return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "(不存在)"; } catch (Exception ex) { return "读取失败: " + ex.Message; } } }
    internal void TestCloseMenu() { _notify.ContextMenuStrip?.Close(); }
    internal void TestAttachPanel(PanelForm p) { _panel = p; p.ApplyReadings(TestSlots); }

    internal void SetAutoStart(bool enable)
    {
        try
        {
            if (enable) AutoStart.Install(); else AutoStart.Remove();
        }
        catch (Exception ex)
        {
            MessageBox.Show("设置开机自启失败：\n" + ex.Message, "外设电量",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // 只读真实状态回填，绝不因为读到一次不一致就替用户删/建启动项
        _autoStart = AutoStart.IsInstalled();
        UpdateAutoStartItem();
        SaveSettings();
        _panel?.SyncSettings();   // 回写面板勾选状态，双向联动
    }

    /// <summary>开机自启的勾选态显示：用 MenuRowItem.CheckState 驱动最左勾选列的自绘浅色 ✓，
    /// 不用 ToolStripMenuItem.Checked（3.1 无法控制其勾选颜色，深底上不可读），也不再用文字前缀，
    /// 避免"系统勾 + 文字前缀"两个对号重复。未勾选时整列留空，与设备固定点行为一致。</summary>
    private void UpdateAutoStartItem()
    {
        _autoStartItem.CheckState = AutoStart.IsInstalled();
    }

    internal void SetOrder(IEnumerable<string> order)
    {
        _order = order.ToList();
        SaveSettings();
        UpdateTray();
    }

    internal IReadOnlyList<string> CurrentOrder()
    {
        lock (_gate)
        {
            var names = _slots.Select(s => s.DisplayName).Distinct().ToList();
            foreach (var n in _order) if (!names.Contains(n)) names.Insert(0, n);
            return ApplyOrder(_slots).Select(s => s.DisplayName).Distinct().ToList();
        }
    }

    // ---------------------------------------------------------- 开机自启

    private void ToggleAutoStart()
    {
        try
        {
            if (_autoStart) { AutoStart.Remove(); _autoStart = false; }
            else { AutoStart.Install(); _autoStart = AutoStart.IsInstalled(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show("设置开机自启失败：\n" + ex.Message, "外设电量",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _autoStart = AutoStart.IsInstalled();
        }
        // 只读真实状态，绝不"纠正"用户的设置
        UpdateAutoStartItem();
        SaveSettings();
        _panel?.SyncSettings();   // 回写面板勾选状态，双向联动
    }

    /// <summary>开机自启真实状态（面板同步用）。</summary>
    internal bool IsAutoStart()
    {
        try { return AutoStart.IsInstalled(); }
        catch { return _autoStart; }
    }

    /// <summary>当前刷新间隔秒数（面板同步用）。</summary>
    internal int CurrentIntervalSec() => _intervalSec;

    // ---------------------------------------------------------- 图标绘制

    // 电量配色阈值（与面板卡片一致）：>=60 绿 / 20-59 黄 / <20 红
    private static Color LevelColor(int? level, bool offline)
    {
        if (offline || !level.HasValue) return Color.FromArgb(150, 150, 155);
        if (level >= 60) return Color.FromArgb(74, 200, 96);
        if (level >= 20) return Color.FromArgb(236, 184, 42);
        return Color.FromArgb(226, 68, 56);
    }

    /// <summary>空心进度环托盘图标（2026-10-26 第四版，用户指定）：
    /// 数字在 16px 下实在看不清 → 改为电量进度环：暗色轨道全环 + 彩色电量弧
    /// （12 点方向起顺时针扫 level/100），颜色即状态（绿/黄/红/充电蓝），离线只画轨道。
    /// 环大线粗（描边 0.15×边长），16px + 32px 双尺寸原生渲染（BuildIcoStream）不发虚。</summary>
    internal static Bitmap RenderIconBmp(int? level, bool offline, bool charging, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var inset = Math.Max(1.5f, size * 0.08f);
            var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
            var stroke = Math.Max(2f, size * 0.15f);   // 16px→2.4、32px→4.8，粗才清晰

            // 轨道（全环）
            using (var pen = new Pen(Color.FromArgb(96, 96, 104), stroke))
                g.DrawEllipse(pen, rect);

            // 电量弧：12 点方向起、顺时针，圆头端帽
            if (!offline && level.HasValue && level.Value > 0)
            {
                var lv = Math.Min(100, level.Value);
                var fg = charging ? BatteryUI.ChargingColor : LevelColor(level, false);
                using (var pen = new Pen(fg, stroke)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                })
                    g.DrawArc(pen, rect, -90f, 360f * lv / 100f);
            }
        }
        return bmp;
    }

    /// <summary>把 16px + 32px 两份位图打包成多尺寸 ICO 字节流。
    /// NotifyIcon.Icon 用 new Icon(stream) 加载——流必须活到图标被替换为止。</summary>
    internal static MemoryStream BuildIcoStream(int? level, bool offline, bool charging)
    {
        var sizes = new[] { 16, 32 };
        var imgs = new List<byte[]>();
        foreach (var s in sizes)
        {
            using var bmp = RenderIconBmp(level, offline, charging, s);
            imgs.Add(BmpToIcoImage(bmp));
        }

        var ms = new MemoryStream();
        W16(ms, 0); W16(ms, 1); W16(ms, (ushort)imgs.Count);   // ICONDIR: reserved/type/count
        var offset = 6 + 16 * imgs.Count;
        for (var i = 0; i < imgs.Count; i++)
        {
            ms.WriteByte((byte)sizes[i]);   // 宽（256 记 0，这里最大 32）
            ms.WriteByte((byte)sizes[i]);   // 高
            ms.WriteByte(0);                // 调色板色数
            ms.WriteByte(0);                // reserved
            W16(ms, 1);                     // planes
            W16(ms, 32);                    // bpp
            W32(ms, (uint)imgs[i].Length);
            W32(ms, (uint)offset);
            offset += imgs[i].Length;
        }
        foreach (var img in imgs) ms.Write(img, 0, img.Length);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Bitmap → ICO 单图数据：BITMAPINFOHEADER(biHeight=2×高) + 自底向上 BGRA + AND 掩码。</summary>
    private static byte[] BmpToIcoImage(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var ms = new MemoryStream();
        W32(ms, 40); W32(ms, (uint)w); W32(ms, (uint)(h * 2)); W16(ms, 1); W16(ms, 32);
        W32(ms, 0); W32(ms, (uint)(w * h * 4)); W32(ms, 0); W32(ms, 0); W32(ms, 0); W32(ms, 0);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // GDI+ 32bppArgb 扫描行内存序就是 B,G,R,A，与 ICO XOR 区一致，行序反转为自底向上
            var row = new byte[w * 4];
            for (var y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                ms.Write(row, 0, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }

        // AND 掩码：每行 w 位按 4 字节对齐，全 0（透明度交给 alpha 通道）
        var maskStride = (w + 31) / 32 * 4;
        ms.Write(new byte[maskStride * h], 0, maskStride * h);
        return ms.ToArray();
    }

    private static void W16(MemoryStream s, ushort v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
    }

    private static void W32(MemoryStream s, uint v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
    }

    // ---------------------------------------------------------- 设置存取

    private string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "BatteryTray", "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { UpdateAutoStartItem(); return; }
            var json = File.ReadAllText(SettingsPath);
            _intervalSec = ReadInt(json, "intervalSec", 60);
            _autoStart = ReadBool(json, "autoStart", AutoStart.IsInstalled());
            var order = ReadStringArray(json, "order");
            if (order != null) _order = order;
            _pinnedKey = ReadString(json, "pinned", "") ?? "";
            foreach (var kv in ReadStringDict(json, "names"))
                _customNames[kv.Key] = kv.Value;
        }
        catch { }
        _timer.Interval = _intervalSec * 1000;
        UpdateAutoStartItem();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var sb = new StringBuilder();
            sb.Append("{\n  \"intervalSec\": ").Append(_intervalSec).Append(",\n");
            sb.Append("  \"autoStart\": ").Append(_autoStart ? "true" : "false").Append(",\n");
            sb.Append("  \"order\": [");
            for (var i = 0; i < _order.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Esc(_order[i])).Append('"');
            }
            sb.Append("],\n");
            sb.Append("  \"pinned\": \"").Append(Esc(_pinnedKey)).Append("\",\n");
            sb.Append("  \"names\": {");
            var first = true;
            foreach (var kv in _customNames)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\n    \"").Append(Esc(kv.Key)).Append("\": \"").Append(Esc(kv.Value)).Append('"');
            }
            sb.Append(first ? "}\n}" : "\n  }\n}");
            File.WriteAllText(SettingsPath, sb.ToString());
        }
        catch { }
    }

    private static string Esc(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>反转义 JSON 字符串（逐字符扫，避免 "\\\\" 与 "\\\"" 互相吞）。</summary>
    private static string Unesc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '\\' || s[i + 1] == '"'))
            {
                sb.Append(s[i + 1]);
                i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static int ReadInt(string json, string key, int def)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : def;
    }

    private static bool ReadBool(string json, string key, bool def)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : def;
    }

    private static List<string> ReadStringArray(string json, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success) return null;
        return System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(x => Unesc(x.Groups[1].Value)).ToList();
    }

    private static string ReadString(string json, string key, string def)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Unesc(m.Groups[1].Value) : def;
    }

    /// <summary>读 "names": { "key": "value" } 字典（key 含反斜杠路径，必须走反转义）。</summary>
    private static Dictionary<string, string> ReadStringDict(string json, string key)
    {
        var result = new Dictionary<string, string>();
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\{([^{}]*)\\}");
        if (!m.Success) return result;
        foreach (System.Text.RegularExpressions.Match p in
                 System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value,
                     "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var k = Unesc(p.Groups[1].Value);
            if (k.Length > 0) result[k] = Unesc(p.Groups[2].Value);
        }
        return result;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _iconStream?.Dispose();
        _tip?.Dispose();
        _notify?.Dispose();
        _timer?.Dispose();
        _panel?.Dispose();
        _window?.Dispose();
    }

    private sealed class HiddenWindow : Form
    {
        public HiddenWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(1, 1);
            Opacity = 0;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
    }
}

}
