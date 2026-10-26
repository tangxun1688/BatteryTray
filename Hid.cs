using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BatteryTray
{

/// <summary>一个已打开的 HID 集合。</summary>
internal sealed class HidInterface : IDisposable
{
    // 设 BATTERYTRAY_TRACE=1 后，--once / --probe 会打印每一次裸 I/O
    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("BATTERYTRAY_TRACE") == "1";

    private static string Hex(byte[] b)
    {
        if (b == null) return "<null>";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < b.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public string Path { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int InLen { get; }
    public int OutLen { get; }
    public bool CanWrite { get; private set; }

    private IntPtr _handle;
    private bool _disposed;

    private HidInterface(string path, ushort vid, ushort pid, ushort up, ushort usage,
                         int inLen, int outLen, IntPtr handle, bool canWrite)
    {
        Path = path; VendorId = vid; ProductId = pid; UsagePage = up; Usage = usage;
        InLen = inLen; OutLen = outLen; _handle = handle; CanWrite = canWrite;
    }

    private static bool IsHandle(IntPtr h) =>
        h != IntPtr.Zero && h != new IntPtr(Native.INVALID_HANDLE_VALUE);

    public static HidInterface TryOpen(string path, bool allowReadOnlyFallback = true)
    {
        foreach (var access in new[] { Native.GENERIC_READ | Native.GENERIC_WRITE, 0 })
        {
            var handle = Native.CreateFileW(path, access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (IsHandle(handle))
            {
                var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
                if (!Native.HidD_GetAttributes(handle, ref attrs)) { Native.CloseHandle(handle); continue; }

                var caps = new Native.HIDP_CAPS();
                if (!Native.HidD_GetPreparsedData(handle, out var prep)) { Native.CloseHandle(handle); continue; }
                try { Native.HidP_GetCaps(prep, out caps); }
                finally { Native.HidD_FreePreparsedData(prep); }

                // 系统独占的顶层集合（鼠标/键盘/笔）只能以 access=0 打开，
                // 能读属性和 Feature Report，但不能发指令。
                var canWrite = access != 0;
                return new HidInterface(path, attrs.VendorID, attrs.ProductID,
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                    caps.OutputReportByteLength, handle, canWrite);
            }

            var err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_ACCESS_DENIED || !allowReadOnlyFallback || access == 0)
                return null;
        }
        return null;
    }

    /// <summary>
    /// 只写请求，不读应答。返回 false = 写入失败（接口被独占、句柄失效），
    /// 这种情况不必重试。
    /// </summary>
    private bool WriteRequest(byte[] request, int timeoutMs)
    {
        var ov = new Native.OVERLAPPED();
        var ev = Native.CreateEventW(IntPtr.Zero, true, false, null);
        if (ev == IntPtr.Zero) return false;
        ov.hEvent = ev;

        try
        {
            var wbuf = Marshal.AllocHGlobal(request.Length);
            try
            {
                Marshal.Copy(request, 0, wbuf, request.Length);
                if (!Native.WriteFile(_handle, wbuf, request.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
                    if (Native.WaitForSingleObject(ev, (uint)timeoutMs) != 0) return false;
                    Native.GetOverlappedResult(_handle, ref ov, out _, false);
                }
            }
            finally { Marshal.FreeHGlobal(wbuf); }
        }
        finally { Native.CloseHandle(ev); }
        return true;
    }

    /// <summary>
    /// 发一个 output report 并读回它对应的应答。
    /// 读写各自使用独立的事件对象 —— 复用同一个 event 会因上一次 IO 完成后
    /// event 仍处于激发态而让等待立刻返回，读到空包。
    /// </summary>
    public byte[] Query(byte[] request, int timeoutMs = 1500)
    {
        if (_disposed || !IsHandle(_handle)) return null;
        if (!CanWrite || request.Length != OutLen || OutLen == 0) return null;

        Drain();
        if (Trace) Console.WriteLine($"[hid {VendorId:X4}:{ProductId:X4} u{Usage:X4}] W {Hex(request)}");
        if (!WriteRequest(request, timeoutMs)) return null;

        return Read(timeoutMs);
    }

    /// <summary>
    /// 发请求并读回应答：最多尝试 <paramref name="tries"/> 条输入报告，
    /// 返回第一条满足 <paramref name="match"/> 的。
    ///
    /// 为什么需要它：无线接收器（罗技 Lightspeed、ROG OMNI）会在应答之间
    /// 夹带设备自发上报的事件报告（本机实测到 11 01 09 00 …、02 63 00 …），
    /// 也可能残留上一条命令的陈旧应答，"写一次读一条"很容易读到噪音。
    /// HID++ 系设备会原样回显请求 byte[3]，OMNI 回显命令字节，
    /// 据此逐条比对才能拿到真正对应的应答。
    ///
    /// 超时语义（本机实测教训）：2.4G 鼠标省电态下应答偶尔超过 1 秒，
    /// 首读只等 900ms 会间歇性失败。因此读超时不等于失败——
    /// 首读给足 timeoutMs，之后每次 400ms 继续收，直到 try 满；
    /// 只有写失败（真错误）才立即返回 null。
    /// </summary>
    public byte[] QueryMatch(byte[] request, Func<byte[], bool> match,
                             int tries = 8, int timeoutMs = 1200)
    {
        if (_disposed || !IsHandle(_handle)) return null;
        if (!CanWrite || request.Length != OutLen || OutLen == 0) return null;

        Drain();
        if (Trace) Console.WriteLine($"[hid {VendorId:X4}:{ProductId:X4} u{Usage:X4}] W {Hex(request)}");
        if (!WriteRequest(request, timeoutMs)) return null;

        for (var i = 0; i < tries; i++)
        {
            // 首读等满超时（设备省电时醒得慢），后续读只用来收噪声后的应答
            var resp = Read(i == 0 ? timeoutMs : 400);
            if (resp != null && match(resp)) return resp;
        }
        return null;
    }

    public byte[] Read(int timeoutMs = 1500)
    {
        if (_disposed || !IsHandle(_handle) || InLen == 0) return null;

        var ov = new Native.OVERLAPPED();
        var ev = Native.CreateEventW(IntPtr.Zero, true, false, null);
        if (ev == IntPtr.Zero) return null;
        ov.hEvent = ev;

        var buf = Marshal.AllocHGlobal(InLen);
        try
        {
            if (!Native.ReadFile(_handle, buf, InLen, out _, ref ov))
            {
                if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING)
                    return null;
                if (Native.WaitForSingleObject(ev, (uint)timeoutMs) != 0)
                {
                    Native.CancelIoEx(_handle, IntPtr.Zero);
                    if (Trace) Console.WriteLine($"[hid {VendorId:X4}:{ProductId:X4} u{Usage:X4}]   R({timeoutMs}) <timeout>");
                    return null;
                }
                if (!Native.GetOverlappedResult(_handle, ref ov, out var got, false))
                    return null;
            }

            var data = new byte[InLen];
            Marshal.Copy(buf, data, 0, InLen);
            if (Trace) Console.WriteLine($"[hid {VendorId:X4}:{ProductId:X4} u{Usage:X4}]   R({timeoutMs}) {Hex(data)}");
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Native.CloseHandle(ev);
        }
    }

    /// <summary>丢弃已排队的输入，确保下一次读到的是本次请求的应答。</summary>
    public void Drain()
    {
        if (Trace) Console.WriteLine($"[hid {VendorId:X4}:{ProductId:X4} u{Usage:X4}]   drain");
        for (var i = 0; i < 8; i++)
        {
            var ov = new Native.OVERLAPPED();
            var ev = Native.CreateEventW(IntPtr.Zero, true, false, null);
            if (ev == IntPtr.Zero) break;
            ov.hEvent = ev;
            var buf = Marshal.AllocHGlobal(InLen);
            try
            {
                if (!Native.ReadFile(_handle, buf, InLen, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) break;
                    if (Native.WaitForSingleObject(ev, 50) != 0)
                    {
                        Native.CancelIoEx(_handle, IntPtr.Zero);
                        break;
                    }
                    Native.GetOverlappedResult(_handle, ref ov, out _, false);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Native.CloseHandle(ev);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsHandle(_handle)) { Native.CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}

/// <summary>枚举本机所有 HID 集合。</summary>
internal static class HidEnum
{
    public static List<HidInterface> EnumerateOpenable(bool allowReadOnlyFallback = true)
    {
        var result = new List<HidInterface>();
        foreach (var path in Paths())
        {
            var dev = HidInterface.TryOpen(path, allowReadOnlyFallback);
            if (dev != null) result.Add(dev);
        }
        return result;
    }

    public static List<string> Paths()
    {
        var paths = new List<string>();
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(Native.INVALID_HANDLE_VALUE)) return paths;

        var detailSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DETAIL_DATAW>();
        var detail = Marshal.AllocHGlobal(detailSize);
        try
        {
            var index = 0;
            while (true)
            {
                var iface = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface))
                    break;
                index++;

                // cbSize 在 x64 上是 8，x86 上是 6；依次尝试直到 SetupAPI 接受
                var ok = false;
                foreach (var cb in new[] { 8, 6 })
                {
                    var scratch = Marshal.AllocHGlobal(detailSize + 1024);
                    try
                    {
                        Marshal.WriteInt32(scratch, cb);
                        if (Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, scratch,
                                detailSize + 1024, out _, IntPtr.Zero))
                        {
                            var p = Marshal.PtrToStringUni(scratch + 4);
                            if (!string.IsNullOrEmpty(p)) { paths.Add(p); ok = true; break; }
                        }
                    }
                    finally { Marshal.FreeHGlobal(scratch); }
                }
                if (!ok) { }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }
}

}
