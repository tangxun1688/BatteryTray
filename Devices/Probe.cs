using System;
using System.Collections.Generic;
using System.Text;

namespace BatteryTray
{
    /// <summary>
    /// 裸 I/O 诊断（--probe）。不复用任何 provider 流程，直接：
    ///   开句柄 → drain 一次 → 发请求 → 把读到的每一条报告都 dump 出来。
    /// 用来把"provider 流程问题"和"裸 I/O 问题"分开。
    /// </summary>
    internal static class Probe
    {
        public static int Run()
        {
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("=== Arctis 1 (1038:12B3) 接口诊断 ===");
            Arctis();

            Console.WriteLine();
            Console.WriteLine("=== 假设A：只开目标集合（同 Python 探针）===");
            Logitech(false);
            Omni(false, withProbe: false);

            Console.WriteLine();
            Console.WriteLine("=== 假设B：先把本机全部 HID 集合打开，再查目标 ===");
            var junk = new List<HidInterface>();
            foreach (var p in HidEnum.Paths())
            {
                var d = HidInterface.TryOpen(p);
                if (d != null) junk.Add(d);
            }
            Console.WriteLine($"  (已打开 {junk.Count} 个句柄)");
            Logitech(true);
            Omni(true, withProbe: false);

            Console.WriteLine();
            Console.WriteLine("=== 假设C：全开句柄 + OMNI 先 probe 再查电量（同 provider 流程）===");
            Omni(true, withProbe: true);

            foreach (var d in junk) d.Dispose();
            return 0;
        }

        /// <summary>
        /// Arctis 1 专用：列出 1038:12B3 的所有 HID 集合，标出 usage/长度/写权限；
        /// 对 0xFF43/0x0202 控制集合发 06 12 并 dump 原始应答。
        /// 用来区分"枚举不到 / 只有只读权限 / 写通了但无应答 / 应答解析问题"。
        /// </summary>
        private static void Arctis()
        {
            foreach (var path in HidEnum.Paths())
            {
                var low = path.ToLowerInvariant();
                if (low.IndexOf("vid_1038", StringComparison.Ordinal) < 0) continue;
                if (low.IndexOf("pid_12b3", StringComparison.Ordinal) < 0) continue;

                var rw = HidInterface.TryOpen(path, allowReadOnlyFallback: false);
                var dev = rw ?? HidInterface.TryOpen(path, allowReadOnlyFallback: true);
                if (dev == null)
                {
                    Console.WriteLine($"  [打不开] {Tail(path)}");
                    continue;
                }
                Console.WriteLine($"  up=0x{dev.UsagePage:X4} usage=0x{dev.Usage:X4} " +
                                  $"in={dev.InLen} out={dev.OutLen} canWrite={dev.CanWrite} {Tail(path)}");
                if (dev.CanWrite && dev.UsagePage == 0xFF43 && dev.Usage == 0x0202)
                {
                    var req = new byte[dev.OutLen];
                    req[0] = 0x06;
                    req[1] = 0x12;
                    Dump("06 12 ->", dev.Query(req, 1500), 16);
                }
                dev.Dispose();
            }
        }

        private static string Tail(string path)
        {
            var i = path.IndexOf("#vid_", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? path : path.Substring(i + 1);
        }

        private static void Dump(string tag, byte[] r, int n = 12)
        {
            if (r == null) { Console.WriteLine($"    {tag}: <null>"); return; }
            var sb = new StringBuilder();
            for (var i = 0; i < r.Length && i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(r[i].ToString("X2"));
            }
            Console.WriteLine($"    {tag}: {sb}");
        }

        private static byte[] Req(HidInterface d, byte rid, byte b1, byte b2,
                                  byte b3 = 0, byte b4 = 0, byte b5 = 0)
        {
            var p = new byte[d.OutLen];
            p[0] = rid; p[1] = b1; p[2] = b2; p[3] = b3; p[4] = b4; p[5] = b5;
            return p;
        }

        private static HidInterface Open(string tag, ushort vid, ushort pid,
                                         ushort up, ushort usage, int minOut, bool multi)
        {
            foreach (var path in HidEnum.Paths())
            {
                var low = path.ToLowerInvariant();
                if (low.IndexOf("vid_" + vid.ToString("x4"), StringComparison.Ordinal) < 0) continue;
                if (low.IndexOf("pid_" + pid.ToString("x4"), StringComparison.Ordinal) < 0) continue;
                var dev = HidInterface.TryOpen(path);
                if (dev == null) continue;
                if (dev.UsagePage != up || dev.Usage != usage || dev.OutLen < minOut || !dev.CanWrite)
                { dev.Dispose(); continue; }
                Console.WriteLine($"  [{tag}] in={dev.InLen} out={dev.OutLen} " +
                                  path.Substring(path.IndexOf("#vid_", StringComparison.OrdinalIgnoreCase) + 1));
                return dev;
            }
            Console.WriteLine($"  [{tag}] 找不到匹配集合");
            return null;
        }

        private static void Logitech(bool multi)
        {
            using var dev = Open("罗技", 0x046D, 0xC547, 0xFF00, 0x0002, 20, multi);
            if (dev == null) return;
            dev.Drain();
            for (var round = 0; round < 3; round++)
            {
                Dump($"r{round} getFeature(0x1004)", dev.Query(Req(dev, 0x11, 0x01, 0x00, 0x01, 0x10, 0x04), 1200), 10);
                Dump($"r{round} caps  (byte3=01) ", dev.Query(Req(dev, 0x11, 0x01, 0x06, 0x01), 1200), 10);
                Dump($"r{round} status(byte3=11) ", dev.Query(Req(dev, 0x11, 0x01, 0x06, 0x11), 1200), 10);
            }
        }

        private static void Omni(bool multi, bool withProbe)
        {
            using var dev = Open("OMNI", 0x0B05, 0x1ACE, 0xFF00, 0x0001, 64, multi);
            if (dev == null) return;
            dev.Drain();
            for (var round = 0; round < 3; round++)
            {
                if (withProbe)
                    Dump($"r{round} probe 02 12 00    ", dev.Query(Req(dev, 0x02, 0x12, 0x00), 1200), 12);
                Dump($"r{round} batt  02 12 01    ", dev.Query(Req(dev, 0x02, 0x12, 0x01), 1200), 12);
            }
        }
    }
}
