using System.Runtime.InteropServices;

namespace WinSweep.Core.Status;

/// <summary>Small Win32 calls used to read machine state without extra dependencies.</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public byte Reserved2;
        public byte Reserved3;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    // Shell file operation, used to send items to the Recycle Bin without any UI.
    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);

    public static ulong ToUInt64(FileTime time) =>
        ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    public static double ToMilliseconds(FileTime time) => ToUInt64(time) * 10.0;
}
