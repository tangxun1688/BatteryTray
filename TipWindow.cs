using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatteryTray
{

/// <summary>
/// 自绘悬浮提示（2026-10-26，替代 NotifyIcon 原生 tooltip）：
/// 原生 tooltip 是比例字体，多列对齐靠空格凑永远有 1-2px 取整误差（用户连续四轮反馈"没对齐"）。
/// 自绘无边框窗口把 名称/百分比/状态 三列画在像素级固定的 x 上，天然对齐，顺便统一深色主题。
/// 无焦点窗口（WS_EX_NOACTIVATE + ShowWithoutActivation），不抢托盘图标 / overflow 面板的交互。
/// 生命周期：NotifyIcon.MouseMove 显示并跟随 → 游标离开图标区域自动隐藏，15s 兜底。
/// 点击窗口 = 打开面板。
/// </summary>
internal sealed class TipWindow : Form
{
    private List<DeviceSlot> _rows = new List<DeviceSlot>();
    private readonly Timer _poll = new Timer { Interval = 250 };
    private Point _hoverPos;          // 最近一次悬停图标的光标位置（静止不动 = 还在看）
    private DateTime _shownAt = DateTime.MinValue;
    private int _xPct;                // 电池列 x（像素级对齐的关键：绘制时不再量测/补格）
    private int _textH;               // 行文本高度（行槽内垂直居中用）

    /// <summary>点击提示窗后的回调（托盘侧接"打开面板"）。</summary>
    public Action Clicked;

    public TipWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = BatteryUI.FormBg;
        DoubleBuffered = true;
        _poll.Tick += (s, e) => PollHide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：永不抢焦点
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt-Tab
            return cp;
        }
    }

    /// <summary>悬停托盘图标时调用：刷新内容并显示/跟随光标。</summary>
    public void ShowTip(List<DeviceSlot> rows, Point cursor)
    {
        _rows = rows;
        _hoverPos = cursor;
        _shownAt = DateTime.Now;
        ComputeLayout();
        Place(cursor);
        if (!Visible) Show();
        Invalidate();
        _poll.Start();
    }

    /// <summary>数据刷新（扫描/改名/固定）时更新内容，不改变位置。</summary>
    public void UpdateRows(List<DeviceSlot> rows)
    {
        if (!Visible) return;
        _rows = rows;
        Invalidate();
    }

    /// <summary>按内容算窗口尺寸和列 x。两列 = 名称 ｜ 电池+百分比（用户：图标已表意，状态文字去掉）。
    /// 字体统一用微软雅黑：Segoe UI 没有汉字，中文回退到雅黑后和英文明显不同大（用户反馈），
    /// 直接整套用雅黑，中英文观感一致。</summary>
    private void ComputeLayout()
    {
        using (var nameFont = new Font(YaHei, 9f))
        using (var pctFontPx = new Font("Segoe UI", 12f * BatScale, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            const TextFormatFlags f = TextFormatFlags.NoPadding;
            var proposed = new Size(short.MaxValue, short.MaxValue);
            int W(string s, Font font) => TextRenderer.MeasureText(s, font, proposed, f).Width;

            var nameW = _rows.Count == 0 ? 0 : _rows.Max(x => W(x.DisplayName, nameFont));
            // 电池列宽 = 电池体 + 极头 + 间隙 + 最宽百分比（"100%"）
            var pctW = _rows.Count == 0 ? 0 : W("100%", pctFontPx);
            var batColW = (int)(30 * BatScale) + 2 + 3 + pctW;
            _xPct = 14 + nameW + 14;
            // 行高 24，文本/电池在各自行槽内垂直居中，上下留边对称（用户反馈边距不一致）
            _textH = W("测试", nameFont);
            ClientSize = new Size(_xPct + batColW + 12, 9 * 2 + Math.Max(1, _rows.Count) * RowH);
        }
    }

    private void Place(Point cursor)
    {
        var va = Screen.FromPoint(cursor).WorkingArea;
        var loc = new Point(cursor.X + 16, cursor.Y + 22);
        if (loc.X + Width > va.Right) loc.X = cursor.X - Width - 8;
        if (loc.Y + Height > va.Bottom) loc.Y = cursor.Y - Height - 8;
        loc.X = Math.Max(va.Left, Math.Min(loc.X, va.Right - Width));
        loc.Y = Math.Max(va.Top, Math.Min(loc.Y, va.Bottom - Height));
        Location = loc;
    }

    private void PollHide()
    {
        if (!Visible) { _poll.Stop(); return; }
        var cur = Cursor.Position;
        // 光标静止在图标上（不移动就不触发 MouseMove）也算"还在看"；
        // 移远（>48px）且不在窗内 → 收起；15s 兜底防 flyout 突然关闭导致的滞留
        var nearIcon = Math.Abs(cur.X - _hoverPos.X) < 48 && Math.Abs(cur.Y - _hoverPos.Y) < 48;
        if ((!nearIcon && !Bounds.Contains(cur)) || (DateTime.Now - _shownAt).TotalSeconds > 15)
            Hide();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Hide();
        Clicked?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        using (var pen = new Pen(BatteryUI.BorderColor))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        using (var nameFont = new Font(YaHei, 9f))
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var s = _rows[i];
                var r = s.Reading;
                var slotY = 9 + i * RowH;
                // 文本/电池都在 24px 行槽内垂直居中 → 首行上方与末行下方留边一致
                var textY = slotY + (RowH - _textH) / 2;
                TextRenderer.DrawText(e.Graphics, s.DisplayName, nameFont,
                    new Point(14, textY), BatteryUI.TextLight);
                var batH = (int)(14 * BatScale);
                BatteryUI.DrawBattery(e.Graphics, new Point(_xPct, slotY + (RowH - batH) / 2),
                    r.Ok ? r.Level : (int?)null, r.Offline, r.Charging, BatScale);
            }
        }
    }

    /// <summary>自验用：把提示窗内容画到位图上（--render 模式核对列对齐 / 电池图标）。</summary>
    internal Bitmap RenderToBitmap(List<DeviceSlot> rows)
    {
        _rows = rows;
        ComputeLayout();
        var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        using (var g = Graphics.FromImage(bmp))
            OnPaint(new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)));
        return bmp;
    }

    /// <summary>中英混排统一字体（Segoe UI 无汉字，回退字号不匹配）。</summary>
    private const string YaHei = "Microsoft YaHei UI";
    /// <summary>提示窗里电池图标的缩放（DrawBattery 1.0 是菜单行尺寸）。</summary>
    private const float BatScale = 0.85f;
    /// <summary>行槽高度：文本/电池在槽内居中，上下留边才对称。</summary>
    private const int RowH = 24;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _poll.Dispose();
        base.Dispose(disposing);
    }
}

}
