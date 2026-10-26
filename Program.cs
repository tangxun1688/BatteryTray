using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BatteryTray
{

internal static class Program
{
    private const string AppName = "外设电量";
    private const string MutexName = "Local\\BatteryTray_SingleInstance_7F3A91";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--once"))
        {
            AllocConsole();
            return RunOnce(args);
        }
        if (args.Contains("--probe"))
        {
            AllocConsole();
            return Probe.Run();
        }
        if (args.Contains("--shot"))
        {
            // 离屏截取真实面板（README 产品图）：DrawToBitmap 不走屏幕，无遮挡/无缩放问题
            AllocConsole();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var panel = new PanelForm(null);
            panel.StartPosition = FormStartPosition.Manual;
            panel.Location = new Point(-20000, -20000);   // 排到屏幕外，瞬显也不打扰
            panel.ApplyReadings(DeviceManager.Scan());
            panel.Show();
            Application.DoEvents();
            Thread.Sleep(400);                            // 等首帧绘制/扫描回调落定
            Application.DoEvents();
            using (var bmp = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
                bmp.Save("shot_panel_ui.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("shot_panel_ui.png saved");
            return 0;
        }
        if (args.Contains("--panel"))
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var panel = new PanelForm(null);
            var slots = DeviceManager.Scan();
            panel.ApplyReadings(slots);
            Application.Run(panel);
            return 0;
        }
        if (args.Contains("--install-startup")) { AutoStart.Install(); return 0; }
        if (args.Contains("--remove-startup")) { AutoStart.Remove(); return 0; }
        if (args.Contains("--menu"))
        {
            // 自验模式：真机截图核对 5 条反馈的落地效果（菜单三态 / tooltip / 固定 / 重命名）
            AllocConsole();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var trayApp = new TrayApp();
            // 必须等消息循环跑起来再 BeginTest：RefreshAsync 的 ContinueWith 用
            // FromCurrentSynchronizationContext() 抓调度器，循环外抓到的是默认上下文，
            // 刷新结果会回到线程池线程而不是 UI 线程。
            var kick = new System.Windows.Forms.Timer { Interval = 1 };
            kick.Tick += (s, e) =>
            {
                kick.Stop();
                kick.Dispose();
                trayApp.BeginTest();
                SelfTest.Run(trayApp);
            };
            kick.Start();
            Application.Run();   // 消息循环；SelfTest 跑完自己 Application.Exit()
            return 0;
        }
        if (args.Contains("--render"))
        {
            // 离线自验：托盘图标 5 状态 / 菜单行三列对齐 / 面板卡片（设备类型简介）/
            // tooltip 名称列对齐。纯控制台渲染，不开窗不动鼠标，跑完即退。
            AllocConsole();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 1) 托盘图标：16px / 32px 原生渲染（ICO 双尺寸），深灰底模拟任务栏
            using (var strip = new Bitmap(5 * 76 + 20, 130))
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(40, 40, 44));
                var states = new Tuple<int?, bool, bool>[]
                {
                    Tuple.Create<int?, bool, bool>(98, false, false),
                    Tuple.Create<int?, bool, bool>(43, false, false),
                    Tuple.Create<int?, bool, bool>(12, false, false),
                    Tuple.Create<int?, bool, bool>(90, false, true),
                    Tuple.Create<int?, bool, bool>(null, true, false),
                };
                for (var i = 0; i < states.Length; i++)
                    using (var bmp16 = TrayApp.RenderIconBmp(states[i].Item1, states[i].Item2, states[i].Item3, 16))
                    using (var bmp32 = TrayApp.RenderIconBmp(states[i].Item1, states[i].Item2, states[i].Item3, 32))
                    {
                        g.DrawImage(bmp16, 20 + i * 76, 20, 16, 16);
                        g.DrawImage(bmp32, 14 + i * 76, 60, 32, 32);
                    }
                strip.Save("render_icon.png");
            }
            Console.WriteLine("render_icon.png: 16px 原生 / 32px 五状态（绿/黄/红/充电蓝/离线灰）");

            // 2) 菜单行（300 宽菜单，内容区 267）：状态文字已去掉，电池色即状态
            DeviceSlot Mock(string name, int? lv, bool off, bool? chg) =>
                new DeviceSlot
                {
                    DisplayName = name,
                    Reading = new Reading { Level = lv, Offline = off, Charging = chg },
                };
            var rows = new[]
            {
                Mock("狗屁王", 97, false, false),
                Mock("ROG夜魔", 99, false, true),
                Mock("Arctis 1", 43, false, null),
                Mock("某台低电量的设备", 12, false, null),
                Mock("ROG 夜魔 (OMNI)", null, true, null),
            };
            using (var strip2 = new Bitmap(300, 30 * rows.Length))
            using (var g = Graphics.FromImage(strip2))
            {
                g.Clear(BatteryUI.FormBg);   // 深色菜单底（浅色文字在白底上不可读）
                for (var i = 0; i < rows.Length; i++)
                    BatteryUI.DrawDeviceRow(g, new Rectangle(33, i * 30, 267, 30),
                        rows[i].DisplayName, rows[i].Reading.Level, rows[i].Reading.Offline,
                        rows[i].Reading.Charging == true, i == 0);
                strip2.Save("render_menu.png");
            }
            Console.WriteLine("render_menu.png: 菜单行 x" + rows.Length + "（无状态列，电池四色）");

            // 3) 面板卡片（简介 = 设备类型；闪电/低电量徽标在状态行文字左边）
            using (var strip3 = new Bitmap(352, 84 * 3 + 16))
            using (var g = Graphics.FromImage(strip3))
            {
                g.Clear(BatteryUI.FormBg);
                BatteryUI.DrawDeviceCard(g, new Rectangle(0, 0, 352, 84),
                    "ROG 夜魔 (OMNI)", "Keyboard", 99, false, true, "充电中", false);
                BatteryUI.DrawDeviceCard(g, new Rectangle(0, 92, 352, 84),
                    "某台低电量的设备", "Mouse", 12, false, false, "使用中", true);
                BatteryUI.DrawDeviceCard(g, new Rectangle(0, 184, 352, 84),
                    "Arctis 1", "Headset", 89, false, false, "使用中（充电状态未知）", false);
                strip3.Save("render_card.png");
            }
            Console.WriteLine("render_card.png: 卡片 x3（充电徽标/低电量徽标/普通）");

            // 4) 自绘悬停提示（TipWindow）：三列像素级对齐 + 菜单同款电池图标 + 雅黑中英混排
            DeviceSlot Tip(string name, int? lv, bool off, bool? chg) =>
                new DeviceSlot
                {
                    DisplayName = name,
                    Reading = new Reading { Level = lv, Offline = off, Charging = chg },
                };
            var tipRows = new[]
            {
                Tip("ROG 夜魔 (OMNI)", 98, false, false),
                Tip("Logitech", 96, false, null),
                Tip("Arctis 1", null, true, null),
                Tip("狗屁王", 18, false, true),
            };
            using (var tw = new TipWindow())
            using (var tipBmp = tw.RenderToBitmap(tipRows.ToList()))
                tipBmp.Save("render_tip.png");
            Console.WriteLine("render_tip.png: 自绘悬浮提示 x" + tipRows.Length + " 行（三列对齐 + 电池图标）");
            return 0;
        }
        if (args.Contains("--icon"))
        {
            // 自验模式：各状态托盘图标放大显示（供截图核对圆环数字版式）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var f = new Form
            {
                Text = "icon states",
                ClientSize = new Size(520, 200),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(420, 300),
                BackColor = Color.DimGray,
            };
            f.Paint += (s, e) =>
            {
                var states = new Tuple<int?, bool, bool>[]
                {
                    Tuple.Create<int?, bool, bool>(98, false, false),
                    Tuple.Create<int?, bool, bool>(90, false, true),
                    Tuple.Create<int?, bool, bool>(43, false, false),
                    Tuple.Create<int?, bool, bool>(12, false, false),
                    Tuple.Create<int?, bool, bool>(null, true, false),
                };
                for (var i = 0; i < states.Length; i++)
                {
                    using (var bmp32 = TrayApp.RenderIconBmp(states[i].Item1, states[i].Item2, states[i].Item3, 32))
                        e.Graphics.DrawImage(bmp32, 20 + i * 100, 30, 32, 32);
                    using (var bmp16 = TrayApp.RenderIconBmp(states[i].Item1, states[i].Item2, states[i].Item3, 16))
                        e.Graphics.DrawImage(bmp16, 20 + i * 100, 90, 16, 16);
                }
            };
            Application.Run(f);
            return 0;
        }
        if (args.Contains("--debug"))
        {
            AllocConsole();
            Console.WriteLine("debug 模式：按 Ctrl+C 退出");
        }

        using var single = new Mutex(true, MutexName, out var isNew);
        if (!isNew)
        {
            MessageBox.Show(AppName + " 已经在运行了。", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var app = new TrayApp();
        app.Start();
        return 0;
    }

    private static int RunOnce(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var raw = args.Contains("--raw");
        var slots = DeviceManager.Scan();
        if (slots.Count == 0)
        {
            Console.WriteLine("未发现可用设备");
            return 2;
        }
        foreach (var s in slots)
        {
            var r = s.Reading;
            var line = $"{s.DisplayName}: ";
            if (r.Offline) line += "未连接";
            else if (r.Ok) line += r.Level + "% " + r.StatusLine;
            else line += r.StatusText;
            if (raw && !string.IsNullOrEmpty(r.Raw)) line += "   [" + r.Raw + "]";
            Console.WriteLine(line);
        }
        return 0;
    }
}

}
