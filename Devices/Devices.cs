using System;
using System.Collections.Generic;

namespace BatteryTray
{

/// <summary>一次电量读取的结果。</summary>
internal sealed class Reading
{
    public int? Level;          // null = 读不到
    public bool? Charging;      // null = 该协议不上报充电状态（不猜）
    public bool Offline;        // 设备关机 / 未连接
    public string Note = "";    // 诊断说明（UI 不再显示，统一三态；--probe/日志用）
    public string Status = "";  // 协议细状态（如"已充满"），仅诊断保留，UI 不再显示
    public string Raw = "";     // 原始报文（调试用，hex）
    public string Source = "";  // 协议出处

    public bool Ok => Level.HasValue;

    /// <summary>
    /// 界面统一三态：使用中 / 充电中 / 休眠中。
    /// 离线、无应答、NAK、读不到电量等一律归为"休眠中"，卡片上不再展开长说明
    /// （用户反馈：长句与其它卡片割裂）。详细原因仍在 Note / Raw 里，供 --probe 排查。
    /// </summary>
    public string StatusText
    {
        get
        {
            if (Offline || !Level.HasValue) return "休眠中";
            if (Charging == true) return "充电中";
            return "使用中";
        }
    }

    /// <summary>面板/托盘/控制台通用的一行状态（三态，无后缀）。</summary>
    public string StatusLine => StatusText;

    /// <summary>悬停 tooltip / 右键菜单用的超短状态（与卡片同一套三态）。</summary>
    public string ShortStatus => StatusText;
}

internal interface IDeviceProvider
{
    string Name { get; }
    string Source { get; }
    /// <summary>设备类型（面板卡片简介用）：Headset / Mouse / Keyboard。</summary>
    string TypeLabel { get; }
    bool Match(HidInterface dev);
    Reading Read(HidInterface dev);
}

// ============================================================ 罗技 HID++ 2.0

/// <summary>
/// Logitech Lightspeed 接收器 046D:C547（GPX2）。
///
/// 协议事实（Linux drivers/hid/hid-logitech-hidpp.c 的 hidpp20_unifiedbattery_*
/// 系列函数 + 本机实测 2026-10-25）：
///   接口：usage_page 0xFF00 / usage 0x0002，输入输出各 20 字节（HID++ LONG report）
///        注意不是同接口下那个 7 字节的 usage 0x0001 集合。
///   设备索引：必须用 0x01（配对的鼠标）；0xFF（接收器自身）不应答。
///   报文：[0]=0x11 [1]=device_index [2]=feature_index
///         [3]=(function &lt;&lt; 4) | software_id   ← 功能号在高 4 位，别拼成 func|sw
///         [4..]=params；设备应答原样回显 byte[3]
///   root.getFeature(0x1004)：func 0x00 → byte[3]=0x01，params {0x10,0x04}
///                           → resp[4]=feature index（实测 0x06）
///   特性 0x1004 UnifiedBattery：
///     func 0x00 getBatteryCapabilities（byte[3]=0x01）
///         resp[4]=支持的电量档位位图（bit0 Critical/bit1 Low/bit2 Good/bit3 Full）
///         resp[5]=标志（bit0 可充电、bit1 有 SoC 百分比）
///     func 0x10 getBatteryStatus（byte[3]=0x11）
///         resp[4]=电量百分比、resp[5]=粗档位（1/2/4/8）、
///         resp[6]=充电状态、resp[7]=外接电源
///   充电状态（内核 hidpp20_unifiedbattery_map_status）：
///         0=放电 1=充电 2=慢充 3=已充满 4=充电错误 其它=未充电
///   实测（鼠标插线）：11 01 06 11 64 08 03 01
///         → 100%、Full、已充满、外接电源=1
///
/// 历史坑（本轮修复）：旧代码用 byte[3]=func|0x01 对 func 0 拼出 0x01 去读
/// "电量"，拿到的其实是 getBatteryCapabilities 的能力位图 0x0f，被误显示成
/// 15%；且"写一次读一条"会撞上接收器夹带的事件报告。
/// </summary>
internal sealed class LogitechProvider : IDeviceProvider
{
    public string Name => "Logitech";
    public string Source => "HID++ 2.0 UnifiedBattery (0x1004)";
    public string TypeLabel => "Mouse";

    private const ushort VID = 0x046D;
    private const ushort PID = 0xC547;
    private const byte DEV_INDEX = 0x01;
    private const byte SW_ID = 0x01;
    private const byte FEATURE_ROOT = 0x00;
    private const byte FUNC_GET_FEATURE = 0x00;
    // byte[3] = (功能号 << 4) | software_id，功能号只占 4 位。
    // 这两个常量是"拼好的功能半字节"（内核里的 CMD_* 值）：0x00=功能0，0x10=功能1。
    // 直接 (func << 4) 会把 0x10 挪成 0x100、截断回 0x01，与功能0撞车——
    // 必须用 (func & 0xF0) 保留原位再或上 software_id。
    private const byte FUNC_GET_CAPABILITIES = 0x00;
    private const byte FUNC_GET_STATUS = 0x10;

    public bool Match(HidInterface d) =>
        d.VendorId == VID && d.ProductId == PID &&
        d.UsagePage == 0xFF00 && d.Usage == 0x0002 && d.OutLen >= 20 && d.CanWrite;

    private static byte[] Build(HidInterface d, byte featIdx, byte func, params byte[] parms)
    {
        var p = new byte[d.OutLen];
        p[0] = 0x11;
        p[1] = DEV_INDEX;
        p[2] = featIdx;
        p[3] = (byte)((func & 0xF0) | SW_ID);
        for (var i = 0; i < parms.Length && 4 + i < p.Length; i++) p[4 + i] = parms[i];
        return p;
    }

    private static string Hex(byte[] b, int n)
    {
        if (b == null) return "<null>";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < b.Length && i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public Reading Read(HidInterface d)
    {
        var r = new Reading { Source = Source };

        // 1) root.getFeature(0x1004) → feature index
        var resp = d.QueryMatch(Build(d, FEATURE_ROOT, FUNC_GET_FEATURE, 0x10, 0x04),
            x => x.Length > 4 && x[0] == 0x11 && x[1] == DEV_INDEX &&
                 x[2] == FEATURE_ROOT && x[3] == 0x01);
        if (resp == null)
        {
            r.Note = "HID++ 无应答（设备可能休眠，或接口被 G HUB 占用）";
            return r;
        }

        var featIdx = resp[4];
        if (featIdx == 0)
        {
            r.Note = "设备不支持 UnifiedBattery";
            return r;
        }

        // 2) getBatteryCapabilities：确认设备是否上报 SoC 百分比
        var cap = d.QueryMatch(Build(d, featIdx, FUNC_GET_CAPABILITIES),
            x => x.Length > 5 && x[0] == 0x11 && x[1] == DEV_INDEX &&
                 x[2] == featIdx && x[3] == 0x01);
        if (cap == null)
        {
            r.Note = "电池能力查询无应答";
            return r;
        }
        var hasSoc = (cap[5] & 0x02) != 0;

        // 3) getBatteryStatus（func 0x10 → byte[3]=0x11）
        var st = d.QueryMatch(Build(d, featIdx, FUNC_GET_STATUS),
            x => x.Length > 7 && x[0] == 0x11 && x[1] == DEV_INDEX &&
                 x[2] == featIdx && x[3] == 0x11);
        if (st == null)
        {
            r.Note = "电量查询无应答";
            return r;
        }

        r.Raw = "feat=0x" + featIdx.ToString("X2") +
                " caps=" + Hex(cap, 8) + " status=" + Hex(st, 8);

        var level = st[4];
        if (level > 100) { r.Note = "电量值越界"; return r; }
        if (!hasSoc)
        {
            // 无 SoC 的设备只报粗档位（1/2/4/8）；内核同样不导出百分比，不猜
            r.Note = "设备仅上报粗档位 0x" + st[5].ToString("X2") + "（无精确百分比）";
            return r;
        }

        r.Level = level;
        switch (st[6])
        {
            case 1:  // charging
            case 2:  // charging slow
                r.Charging = true;
                break;
            case 3:  // complete
                r.Charging = false;
                r.Status = "已充满";
                break;
            case 4:  // charging error
                r.Charging = false;
                r.Status = "充电错误";
                break;
            case 0:  // discharging
                r.Charging = false;
                break;
            default:
                r.Charging = false;
                r.Status = "电池状态异常";
                break;
        }
        return r;
    }
}

// ============================================================ ROG OMNI 夜魔

/// <summary>
/// ASUS ROG Azoth 绑在 ROG OMNI 接收器 (0B05:1ACE) 上。
///
/// 协议事实（seerge/g-helper GPLv3, app/Peripherals/Keyboard/Models/Azoth.cs 的
/// AzothOmni 变体 + 本机实测 2026-10-25 读到 43%/55%）：
///   接口：mi_02&col02，usage_page 0xFF00 / usage 0x0001，输入输出各 64 字节
///        （col01=0xFF02、col03=0xFF01 都不是控制接口）
///   reportId = 0x02
///   探针  02 12 00：有效应答 = resp[1]==0x12 && resp[2]==0x00 && resp[5]!=0xFA(0xFA=NAK)
///   电量  02 12 01：resp[6]=百分比、resp[9]==1 表示充电、resp[7]=休眠超时
///   NAK   02 FF AA：设备未就绪。g-helper WriteForResponseLocked 同样直接
///         return null；实测键盘空闲休眠后电量查询 100% NAK（探针仍通），
///         唤醒后恢复——按任意键即可，属设备侧省电行为。
///   接收器还会自发推送事件报告（实测见到 02 63 00 …、02 12 03 …），必须多读几条
///   按请求头回显（02 12 <cmd>）匹配，不能取第一条。
/// </summary>
internal sealed class AsusOmniProvider : IDeviceProvider
{
    public string Name => "ROG 夜魔 (OMNI)";
    public string Source => "g-helper AzothOmni (GPLv3)";
    public string TypeLabel => "Keyboard";

    private const ushort VID = 0x0B05;
    private const ushort PID = 0x1ACE;
    private const byte REPORT_ID = 0x02;

    public bool Match(HidInterface d) =>
        d.VendorId == VID && d.ProductId == PID &&
        d.UsagePage == 0xFF00 && d.Usage == 0x0001 && d.OutLen >= 64 && d.CanWrite;

    private static byte[] Build(HidInterface d, byte cmd)
    {
        var p = new byte[d.OutLen];
        p[0] = REPORT_ID;
        p[1] = 0x12;
        p[2] = cmd;
        return p;
    }

    private static string Hex(byte[] b, int n)
    {
        if (b == null) return "<null>";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < b.Length && i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public Reading Read(HidInterface d)
    {
        var r = new Reading { Source = Source };

        var probe = d.QueryMatch(Build(d, 0x00),
            x => x.Length >= 6 && x[0] == REPORT_ID && x[1] == 0x12 && x[2] == 0x00);
        if (probe == null || probe[5] == 0xFA)
        {
            r.Note = "OMNI 控制接口未应答";
            return r;
        }

        var b = d.QueryMatch(Build(d, 0x01),
            x => x.Length >= 3 && x[0] == REPORT_ID &&
                 ((x[1] == 0x12 && x[2] == 0x01) || (x[1] == 0xFF && x[2] == 0xAA)));
        // NAK（02 FF AA）= 键盘未就绪。g-helper 的 WriteForResponseLocked 同样把
        // FF AA 当无应答直接 return null——休眠/关机后接收器代答不了电量查询。
        // 本机 2026-10-25 实测：空闲数小时后电量查询 100% NAK，探针仍通；
        // 唤醒键盘（按任意键）后恢复，属设备侧行为，非协议解析问题。
        if (b != null && b[1] == 0xFF && b[2] == 0xAA)
        {
            r.Raw = Hex(b, 4);
            r.Note = "键盘未就绪（NAK：可能休眠或已关机，按任意键唤醒）";
            return r;
        }
        if (b == null)
        {
            r.Note = "电量查询无应答";
            return r;
        }

        r.Raw = Hex(b, 12);
        var level = b[6];
        if (level > 100) { r.Note = "电量值越界"; return r; }
        r.Level = level;
        r.Charging = b[9] == 1;
        return r;
    }
}

// ============================================================ 赛睿 Arctis 1

/// <summary>
/// SteelSeries Arctis 1 Wireless (1038:12B3)。
///
/// 协议事实（三方上游交叉一致 + 本机实测 2026-10-25）：
///   - HeadsetControl lib/devices/steelseries_arctis_1.hpp：请求 {0x06,0x12}，
///     电量字节=[3]、状态字节=[2]、离线值=0x01、**充电值=0xFF（该型号没有）**
///   - Linux drivers/hid/hid-steelseries-arctis.c（2026 年仍在维护）：
///     data[2] != 0x01 → 已连接；data[3] → 电量；**从不解析充电**
///     （同驱动的 Arctis 9 / Nova 系都解析 battery_charging，唯独 1 和 7 不解析）
///   - rigbat docs/headsets.md 汇总：「Charging: not reported」
///   接口：mi_03 &amp; col02，usage_page 0xFF43 / usage 0x0202，输入输出各 31 字节
///        （同接口 col03 的 0xFF00/0x0001 写入报 ERROR_INVALID_FUNCTION，不能用）
///   请求 06 12；响应 06 12 &lt;status&gt; &lt;level&gt; ...
///
/// 充电识别（2026-10-26 复测定案）：
///   - 该型号的 06 12 应答**不包含充电状态**：83% 时充电与未充电的整包完全相同
///     （06 12 03 53 00 C0-C2 0F ...），byte[5] 不是充电标志（更像电压/信号类数值）。
///   - HeadsetControl 把 charging_value 设为 0xFF 哨兵（注释 "no charging for this device"）；
///     Linux hid-steelseries-arctis.c 也不解析该型号的充电。
///   - 因此不再猜测：Charging 恒为 null，UI 显示"使用中"；只有离线/无应答才显示"休眠中"。
///   - 原始报文仍可在 --raw / --probe 查看。
/// </summary>
internal sealed class SteelSeriesProvider : IDeviceProvider
{
    public string Name => "Arctis 1 Wireless";
    public string Source => "HeadsetControl / hid-steelseries-arctis";
    public string TypeLabel => "Headset";

    private const ushort VID = 0x1038;

    // 不用静态数组：本机 net48 构建下该数组在进程启动时前 3 个元素就是垃圾值
    // （反射实测 01EB,0000,E0FF,12D5，且每次进程不同），导致 Match 永远 false、
    // Arctis 时有时无。改成显式比较，绕开这个静态数组初始化异常。
    private static bool IsKnownPid(ushort pid) =>
        pid == 0x12B3 || pid == 0x12B6 || pid == 0x12D7 || pid == 0x12D5;

    // 该型号不上报充电状态（见类注释），无需趋势状态字段。

    public bool Match(HidInterface d) =>
        d.VendorId == VID && IsKnownPid(d.ProductId) &&
        d.UsagePage == 0xFF43 && d.Usage == 0x0202 && d.CanWrite;

    private static string Hex(byte[] b, int n)
    {
        if (b == null) return "<null>";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < b.Length && i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public Reading Read(HidInterface d)
    {
        var r = new Reading { Source = Source };
        var req = new byte[d.OutLen];
        req[0] = 0x06;
        req[1] = 0x12;

        var resp = d.QueryMatch(req, x => x.Length >= 4 && x[0] == 0x06 && x[1] == 0x12);
        if (resp == null)
        {
            r.Note = "dongle 无应答";
            return r;
        }

        // 多打 16 字节：状态字节/[5][6] 电压之外可能还有字段，留着后续逆向用
        r.Raw = Hex(resp, 16);

        if (resp[2] == 0x01)
        {
            r.Offline = true;
            r.Note = "耳机关机或超出范围";
            return r;
        }

        var level = resp[3];
        if (level > 100)
        {
            // 重连瞬间会读到脏值（实测 0x6C=108，下一轮立刻正常）。立刻重试一次，
            // 别把瞬时噪声当成"电量值越界"报给用户。
            var again = d.QueryMatch(req, x => x.Length >= 4 && x[0] == 0x06 && x[1] == 0x12);
            if (again == null) { r.Note = "dongle 无应答"; return r; }
            r.Raw = "retry " + Hex(again, 16) + " | " + r.Raw;
            level = again[3];
            if (again[2] == 0x01)
            {
                r.Offline = true;
                r.Note = "耳机关机或超出范围";
                return r;
            }
            if (level > 100) { r.Note = "电量值越界"; return r; }
        }
        r.Level = level;

        // ---- 充电状态：该协议不提供，不再猜测 ----
        // 实测 2026-10-26：83% 时充电与未充电的整包完全相同
        //   （06 12 03 53 00 C0-C2 0F ...），byte[5] 不是充电标志。
        // HeadsetControl 用 0xFF 哨兵声明该型号无充电；Linux 驱动同样不解析。
        // Charging 保持 null → UI 显示"使用中"；离线/无应答才显示"休眠中"。
        r.Charging = null;
        return r;
    }
}

// ============================================================ 设备管理

internal sealed class DeviceSlot
{
    public string Key = "";
    public string DisplayName = "";
    public string DefaultName = "";  // 扫描时的协议默认名（自定义名留空时恢复它）
    public string Source = "";
    public string TypeLabel = "";    // 设备类型：Headset / Mouse / Keyboard（面板简介用）
    public Reading Reading = new Reading();
    public DateTime LastUpdate;
    public int ConsecutiveFailures;
}

internal sealed class DeviceManager
{
    // 顺序即优先级；加新设备 = 加一个 provider，UI 和轮询不用动
    private static readonly List<IDeviceProvider> Providers = new List<IDeviceProvider>()
    {
        new LogitechProvider(),
        new AsusOmniProvider(),
        new SteelSeriesProvider(),
    };

    public static IReadOnlyList<IDeviceProvider> AllProviders => Providers;

    /// <summary>扫一遍，返回当前实际匹配上的槽位。</summary>
    public static List<DeviceSlot> Scan()
    {
        var slots = new List<DeviceSlot>();
        var seen = new HashSet<string>();

        // 逐个打开、用完即关：不能把所有集合的句柄同时攥在手里。
        // Arctis 1（1038:12B3）的 MI_03&COL02 控制集合，在 COL01/COL03 已被以读写方式
        // 打开时会退化成只读（CanWrite=false），SteelSeriesProvider 就匹配不上，
        // 表现为"有时能读到、有时整台设备消失"。--probe 单独打开每个集合可稳定读到。
        foreach (var path in HidEnum.Paths())
        {
            using var dev = HidInterface.TryOpen(path);
            if (dev == null) continue;
            foreach (var p in Providers)
            {
                if (!p.Match(dev)) continue;
                // 稳定硬件标识（VID:PID:UsagePage:Usage）作 Key——不含易变的友好名串与 HID
                // 路径，改名/重插/休眠重枚举都不会让已存自定义名失配（根治"改名每次都重置"）。
                var key = $"{dev.VendorId:X4}:{dev.ProductId:X4}:{dev.UsagePage:X4}:{dev.Usage:X4}";
                if (!seen.Add(key)) continue;

                var slot = new DeviceSlot
                {
                    Key = key,
                    DisplayName = p.Name,
                    DefaultName = p.Name,
                    Source = p.Source,
                    TypeLabel = p.TypeLabel,
                };
                try { slot.Reading = p.Read(dev); }
                catch (Exception ex) { slot.Reading = new Reading { Note = "异常: " + ex.Message }; }
                slot.LastUpdate = DateTime.Now;
                slots.Add(slot);
            }
        }
        return slots;
    }
}

internal sealed class CompositeDisposable : IDisposable
{
    private readonly List<IDisposable> _items = new List<IDisposable>();
    public void Add(IDisposable item) => _items.Add(item);
    public void Dispose() { foreach (var i in _items) { try { i.Dispose(); } catch { } } }
}

}
