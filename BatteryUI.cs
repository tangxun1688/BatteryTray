using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BatteryTray
{
    /// <summary>
    /// 电量视觉共享件：配色阈值 + 手机状态栏式横向电池图标 + 深色面板主题。
    /// 面板卡片（长条）、右键菜单行、托盘弹窗行都走这里，保证同一套设计语言。
    /// 托盘悬停是原生 tooltip、纯文本画不了图，用 Unicode 方块条近似（见 TrayApp.BuildTip）。
    /// </summary>
    internal static class BatteryUI
    {
        // ---------- 电量阈值（全套统一）：>=60 绿 / 20-59 黄 / <20 红 ----------

        /// <summary>浅色底/图标上的电量色。</summary>
        public static Color LevelColor(int level) =>
            level >= 60 ? Color.FromArgb(74, 200, 96) :
            level >= 20 ? Color.FromArgb(236, 184, 42) :
                          Color.FromArgb(226, 68, 56);

        /// <summary>白底上的深色文字变体。</summary>
        public static Color DimColor(int level) =>
            level >= 60 ? Color.FromArgb(36, 142, 66) :
            level >= 20 ? Color.FromArgb(178, 132, 16) :
                          Color.FromArgb(198, 52, 42);

        /// <summary>深色底（面板长条/状态点）上的亮色变体。</summary>
        public static Color BarColor(int level) =>
            level >= 60 ? Color.FromArgb(64, 214, 108) :
            level >= 20 ? Color.FromArgb(245, 190, 50) :
                          Color.FromArgb(240, 78, 66);

        public static readonly Color OfflineColor = Color.FromArgb(148, 148, 155);
        public static readonly Color LowRed = Color.FromArgb(240, 78, 66);
        /// <summary>充电状态色（用户指定的四色逻辑：绿/黄/红/充电蓝）。</summary>
        public static readonly Color ChargingColor = Color.FromArgb(56, 148, 255);
        private static readonly Color FrameColor = Color.FromArgb(88, 88, 96);
        private static readonly Color BoltColor = Color.FromArgb(255, 214, 64);

        /// <summary>电池填充色：充电中一律蓝色，其余按电量绿/黄/红（用户四色逻辑）。</summary>
        public static Color BatteryFill(int? level, bool offline, bool charging)
        {
            if (offline || !level.HasValue) return OfflineColor;
            if (charging) return ChargingColor;
            return LevelColor(level.Value);
        }

        // ---------- 深色面板主题 ----------
        public static readonly Color FormBg = Color.FromArgb(22, 22, 27);
        public static readonly Color CardBg = Color.FromArgb(35, 35, 43);
        public static readonly Color GroupBg = Color.FromArgb(35, 35, 43);
        public static readonly Color BorderColor = Color.FromArgb(52, 52, 63);
        public static readonly Color TextLight = Color.FromArgb(238, 238, 244);
        public static readonly Color TextDim = Color.FromArgb(140, 140, 154);
        public static readonly Color CtrlBg = Color.FromArgb(44, 44, 54);
        public static readonly Color SelBg = Color.FromArgb(56, 56, 68);
        public static readonly Color TrackColor = Color.FromArgb(56, 56, 66);
        public static readonly Color LowText = Color.FromArgb(240, 150, 140);

        // ---------- 浅色菜单主题 ----------
        // 悬停高亮不再自绘：行项是真正的 ToolStripMenuItem，base.OnPaint 走视觉样式
        // 渲染器画选择背景（#B3D7F3 + 强调色边缘，全宽），与原生菜单项完全一致。
        public static readonly Color MenuAccent = Color.FromArgb(0, 120, 215);
        public static readonly Color MenuText = Color.FromArgb(32, 32, 38);
        public static readonly Color MenuDim = Color.FromArgb(120, 120, 132);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            if (d <= 0 || r.Width < d || r.Height < d)
            {
                p.AddRectangle(r);
                p.CloseFigure();
                return p;
            }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>画一个闪电（单位坐标多边形，中心在原点）。</summary>
        private static void Bolt(Graphics g, float cx, float cy, float h)
        {
            var pts = new[]
            {
                new PointF(cx + 0.16f * h, cy - 0.92f * h),
                new PointF(cx - 0.46f * h, cy + 0.10f * h),
                new PointF(cx - 0.06f * h, cy + 0.10f * h),
                new PointF(cx - 0.20f * h, cy + 0.92f * h),
                new PointF(cx + 0.46f * h, cy - 0.16f * h),
                new PointF(cx + 0.06f * h, cy - 0.16f * h),
            };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                path.CloseFigure();
                using (var b = new SolidBrush(BoltColor))
                using (var pen = new Pen(Color.FromArgb(120, 60, 0), Math.Max(0.8f, h * 0.06f)))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>充电小闪电徽标（画在指定矩形内）。</summary>
        public static void DrawBoltBadge(Graphics g, Rectangle r)
        {
            Bolt(g, r.X + r.Width / 2f, r.Y + r.Height / 2f, r.Height * 0.46f);
        }

        /// <summary>低电量徽标（红底圆形 + 白色感叹号，面板状态行用，约 11x11）。</summary>
        public static void DrawLowBadge(Graphics g, int x, int y)
        {
            using (var b = new SolidBrush(LowRed))
                g.FillEllipse(b, x, y + 1, 11, 11);
            using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                TextRenderer.DrawText(g, "!", f, new Rectangle(x, y, 12, 13),
                    Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 紧凑横向电池 + 右侧百分比（右键菜单行用）。返回占用的总宽度
        /// （含电池体 30px —— 早期版本漏了电池体宽，导致调用方把状态文字
        /// 压到百分比上，出现"98使用中"重叠）。
        /// </summary>
        public static Size DrawBattery(Graphics g, Point loc, int? level, bool offline, bool? charging, float scale)
        {
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;   // 小圆角填充不开抗锯齿会有毛边
            var bw = (int)(30 * scale);
            var bh = (int)(14 * scale);
            var nubW = Math.Max(2, (int)(2 * scale));
            var gap = Math.Max(3, (int)(3 * scale));
            var body = new Rectangle(loc.X, loc.Y, bw, bh);

            // 外框 + 正极小凸头
            using (var path = Rounded(body, Math.Max(2, (int)(3 * scale))))
            using (var pen = new Pen(FrameColor, Math.Max(1f, 1.4f * scale)))
                g.DrawPath(pen, path);
            using (var b = new SolidBrush(FrameColor))
                g.FillRectangle(b, body.Right + 1, body.Y + body.Height / 2 - Math.Max(1, (int)(2 * scale)),
                                nubW, Math.Max(2, (int)(4 * scale)));

            var pctText = "--";
            Color pctColor = OfflineColor;
            Color fillColor = OfflineColor;

            if (!offline && level.HasValue)
            {
                var lv = Math.Max(0, Math.Min(100, level.Value));
                pctText = lv + "%";
                pctColor = charging == true ? ChargingColor : DimColor(lv);
                fillColor = BatteryFill(level, offline, charging == true);

                // 填充（内缩防蹭框）
                var inner = new Rectangle(body.X + Math.Max(1, (int)(1.5f * scale)),
                                          body.Y + Math.Max(1, (int)(1.5f * scale)),
                                          bw - 2 * Math.Max(1, (int)(1.5f * scale)),
                                          bh - 2 * Math.Max(1, (int)(1.5f * scale)));
                var fw = (int)Math.Round(inner.Width * lv / 100.0);
                if (fw > 0)
                {
                    var fill = new Rectangle(inner.X, inner.Y, Math.Max(fw, (int)(2 * scale)), inner.Height);
                    using (var path = Rounded(fill, Math.Max(1, (int)(1.5f * scale))))
                    using (var b = new SolidBrush(fillColor))
                        g.FillPath(b, path);
                }
                // 充电闪电画在电池中央
                if (charging == true) Bolt(g, body.X + body.Width / 2f, body.Y + body.Height / 2f, bh * 0.42f);
            }

            // 右侧百分比
            var pctFont = new Font("Segoe UI", 12 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            var pctSize = TextRenderer.MeasureText(g, pctText, pctFont);
            TextRenderer.DrawText(g, pctText, pctFont,
                new Point(body.Right + gap + nubW, body.Y + (body.Height - pctSize.Height) / 2),
                pctColor);
            pctFont.Dispose();

            // 注意：含电池体宽（调用方常把它当作"电池+百分比"整体占位）
            g.SmoothingMode = oldSmooth;
            return new Size(bw + nubW + gap + pctSize.Width, Math.Max(bh, pctSize.Height));
        }

        /// <summary>
        /// 全宽长条电量（面板卡片）：圆角轨道 + 按电量填充。
        /// 填充裁剪进轨道的圆角路径里（左右端与轨道完全贴合）+ 抗锯齿——
        /// 早期直接 FillPath 一个独立圆角矩形，两端和轨道对不齐，看起来有缺口。
        /// 充电中整条变蓝、闪电画在条上（用户四色逻辑）。
        /// level=null 或 offline 时只画空轨道。
        /// </summary>
        public static void DrawBar(Graphics g, Rectangle r, int? level, bool offline, bool charging = false)
        {
            var radius = Math.Min(4, r.Height / 2);
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = Rounded(r, radius))
            using (var b = new SolidBrush(TrackColor))
                g.FillPath(b, track);

            if (offline || !level.HasValue || level.Value <= 0)
            {
                g.SmoothingMode = oldSmooth;
                return;
            }
            var lv = Math.Max(0, Math.Min(100, level.Value));
            var fw = (int)Math.Round(r.Width * lv / 100.0);
            fw = Math.Max(Math.Min(6, r.Width), Math.Min(fw, r.Width)); // 至少露出一小截
            using (var clip = Rounded(r, radius))
            {
                g.SetClip(clip);
                using (var b = new SolidBrush(charging ? ChargingColor : BarColor(lv)))
                    g.FillRectangle(b, r.X, r.Y, fw, r.Height);
                if (charging)
                    DrawBoltBadge(g, new Rectangle(r.X + r.Width / 2 - 6, r.Y + r.Height / 2 - 7, 12, 14));
                g.ResetClip();
            }
            g.SmoothingMode = oldSmooth;
        }

        /// <summary>超长文本截断（加 …），返回不超过 maxW 的字符串。</summary>
        public static string Truncate(string text, Font font, int maxW)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (TextRenderer.MeasureText(text, font).Width <= maxW) return text;
            var t = text;
            while (t.Length > 1 && TextRenderer.MeasureText(t + "…", font).Width > maxW)
                t = t.Substring(0, t.Length - 1);
            return t + "…";
        }

        // ---------------------------------------------------------- 整行/整卡绘制

        /// <summary>四角星（固定标记用，单位坐标多边形，中心在原点）。</summary>
        public static void DrawStar(Graphics g, float cx, float cy, float r, Color color)
        {
            var k = r * 0.26f;   // 内缩比：小四角星
            var pts = new[]
            {
                new PointF(cx, cy - r),
                new PointF(cx + k, cy - k),
                new PointF(cx + r, cy),
                new PointF(cx + k, cy + k),
                new PointF(cx, cy + r),
                new PointF(cx - k, cy + k),
                new PointF(cx - r, cy),
                new PointF(cx - k, cy - k),
            };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                path.CloseFigure();
                using (var b = new SolidBrush(color))
                    g.FillPath(b, path);
            }
        }

        /// <summary>
        /// 面板设备卡片（深色主题，352x84 左右）：
        /// 左上名称+协议出处，右上大号百分比（充电带闪电），中部全宽长条，底部状态行。
        /// pinned=true 时名称前画固定星标（该设备正显示在托盘图标上）。
        /// </summary>
        public static void DrawDeviceCard(Graphics g, Rectangle r, string name, string source,
                                          int? level, bool offline, bool charging, string status,
                                          bool pinned = false)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var ok = level.HasValue && !offline;
            var pad = 18;

            // 圆角卡片底（控件四角露 FormBg，所以这里必须画圆角）
            using (var path = Rounded(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 10))
            using (var b = new SolidBrush(CardBg))
                g.FillPath(b, path);

            var nameX = r.X + pad;
            // 固定星标（名称左侧，强调色）
            if (pinned) DrawStar(g, nameX + 5, r.Y + 17, 5.5f, MenuAccent);

            // 名称（左上）：微软雅黑——中英混排（如"ROG 夜魔 (OMNI)"）字号观感一致
            var nameFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            TextRenderer.DrawText(g, Truncate(name, nameFont, 176), nameFont,
                new Point(nameX + (pinned ? 14 : 0), r.Y + 10), TextLight);
            nameFont.Dispose();

            // 协议出处（名称下方）
            var srcFont = new Font("Segoe UI", 8f);
            TextRenderer.DrawText(g, Truncate(source, srcFont, 190), srcFont,
                new Point(r.X + pad, r.Y + 31), TextDim);
            srcFont.Dispose();

            // 大号百分比（右上）：26px 数字 + 13px "%"，低电量转红
            var pctText = ok ? level.Value.ToString() : "--";
            var numFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
            var sufFont = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            var numW = TextRenderer.MeasureText(pctText, numFont).Width;
            var sufW = ok ? TextRenderer.MeasureText("%", sufFont).Width + 4 : 0;
            var pctColor = !ok ? TextDim : level.Value < 20 ? LowRed : TextLight;
            var px = r.Right - pad - numW - sufW;
            TextRenderer.DrawText(g, pctText, numFont, new Point(px, r.Y + 8), pctColor);
            if (ok) TextRenderer.DrawText(g, "%", sufFont, new Point(px + numW + 4, r.Y + 19), pctColor);
            numFont.Dispose();
            sufFont.Dispose();

            // 全宽长条电量：充电中整条变蓝 + 闪电在条上
            DrawBar(g, new Rectangle(r.X + pad, r.Y + 52, r.Width - pad * 2, 8), level, offline, charging && ok);

            // 状态行（多为中文，如"使用中（充电状态未知）"）
            var stFont = new Font("Microsoft YaHei UI", 8f);
            var stColor = ok && level.Value < 20 ? LowText : TextDim;
            var stX = r.X + pad;
            if (ok && level.Value < 20)
            {
                DrawLowBadge(g, stX, r.Y + 63);
                stX += 16;
            }
            TextRenderer.DrawText(g, Truncate(status, stFont, r.Right - pad - stX), stFont,
                new Point(stX, r.Y + 64), stColor);
            stFont.Dispose();
        }

    /// <summary>
    /// 菜单里统一的"选中"对勾：开机自启、设备固定（选中态）共用同一图标与配色，
    /// 落在最左勾选列中心（深底上用浅色 TextLight 才清晰）。
    /// </summary>
    public static void DrawCheck(Graphics g, Point center)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(TextLight, 1.8f))
            g.DrawLines(pen, new[]
            {
                new Point(center.X - 4, center.Y),
                new Point(center.X - 1, center.Y + 3),
                new Point(center.X + 5, center.Y - 4),
            });
    }

    /// <summary>
    /// 右键菜单里的设备行（菜单宽 300，行项 bounds 横跨整个菜单宽）。
    /// 状态文字已去掉（用户：图标本身表明状态）——电池填充色即状态：
    /// 绿(≥60)/黄(20-59)/红(<20)/蓝+闪电(充电中)/灰(离线)，见 BatteryFill。
    /// 布局（绝对坐标，与下方操作项 MenuRowItem 共用同一套左对齐）：
    ///   · 最左勾选列中心 x=10：被"固定/选中"的设备画一个浅色对勾（DrawCheck），
    ///     与"开机自启"的 ✓ 同一图标、同一配色（用户要求统一，不再用实心点）。
    ///   · 名称起点 x=26（统一左对齐）｜ 电池+百分比 x=200。
    /// 悬停高亮由菜单渲染器画（调用方 base.OnPaint），这里只管内容。
    /// </summary>
    public static void DrawDeviceRow(Graphics g, Rectangle r, string name, int? level,
                                     bool offline, bool charging, bool pinned)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        const int checkCx = 13;    // 勾选列中心（勾选列 0..26 的正中，用户要求居中展示）
        const int textX = 26;      // 名称/文字起点：与 MenuRowItem 统一左对齐
        const int batteryX = 200;  // 电池图标起点

        // 选中/固定：与开机自启同一个浅色对勾（图标统一）
        if (pinned)
            DrawCheck(g, new Point(checkCx, r.Y + r.Height / 2));

        // 名称：浅色（菜单整体深色主题，见 MenuTheme）
        var nameFont = new Font("Microsoft YaHei UI", 9.5f);
        TextRenderer.DrawText(g, Truncate(name, nameFont, 150), nameFont,
            new Point(textX, r.Y + 7), TextLight);
        nameFont.Dispose();

        // 横向电池 + 百分比（DrawBattery 返回宽度含电池体）
        DrawBattery(g, new Point(batteryX, r.Y + 8), level, offline, charging, 1f);
    }
    }
}
