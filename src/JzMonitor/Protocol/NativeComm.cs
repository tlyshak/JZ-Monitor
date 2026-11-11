using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace JzMonitor.Protocol;

/// <summary>Мінімальний P/Invoke до Win32 Comm API — так само, як працював оригінальний CarTester.</summary>
internal static class NativeComm
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint EV_CTS = 0x0008, EV_DSR = 0x0010;
    public const uint MS_CTS_ON = 0x0010, MS_DSR_ON = 0x0020;
    public const uint PURGE_ALL = 0x000F;
    public const uint SETDTR = 5, CLRDTR = 6, SETRTS = 3, CLRRTS = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EvtChar;
        public ushort wReserved1;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetCommMask(SafeFileHandle h, uint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WaitCommEvent(SafeFileHandle h, out uint mask, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetCommModemStatus(SafeFileHandle h, out uint status);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool EscapeCommFunction(SafeFileHandle h, uint func);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PurgeComm(SafeFileHandle h, uint flags);

    public static void ThrowLast(string what) => throw new Win32Exception(Marshal.GetLastWin32Error(), what);

    /// <summary>Список COM-портів з реєстру (без залежності від System.IO.Ports).</summary>
    public static string[] GetPortNames()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key == null) return [];
            return key.GetValueNames()
                .Select(n => key.GetValue(n) as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct()
                .OrderBy(s => s.Length).ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
