using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray
{

/// <summary>
/// 开机自启：在「启动」文件夹里放一个 .lnk。
/// 这样任务管理器 → 启动 标签里能直接看到、能启用/禁用，比注册表 Run 更直观。
/// 全部用 COM 互操作手工声明，零第三方依赖。
/// </summary>
internal static class AutoStart
{
    private const string LnkName = "外设电量.lnk";

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"),
     ClassInterface(ClassInterfaceType.None)]
    private class ShellLink { }

    public static string LnkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), LnkName);

    public static bool IsInstalled()
    {
        try { return File.Exists(LnkPath); }
        catch { return false; }
    }

    public static void Install()
    {
        var target = Process.GetCurrentProcess().MainModule.FileName;
        if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("取不到当前程序路径");

        var args = "";
        // 源码方式运行（dotnet BatteryTray.dll）时，快捷方式要指向 dotnet.exe + dll 参数
        if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            args = target;
            target = "dotnet";
        }

        var link = (IShellLinkW)new ShellLink();
        link.SetDescription("外设电量监控托盘");
        link.SetWorkingDirectory(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
        link.SetPath(target);
        ((IPersistFile)link).Save(LnkPath, true);
        Marshal.FinalReleaseComObject(link);
    }

    public static void Remove()
    {
        if (File.Exists(LnkPath)) File.Delete(LnkPath);
    }
}

}
