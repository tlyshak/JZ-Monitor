using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static JzMonitor.Protocol.NativeComm;

namespace JzMonitor.Protocol;

/// <summary>
/// Читає діагностичний сигнал ECU через лінію DSR COM-порту, як і оригінал:
/// WaitCommEvent(EV_DSR) → GetCommModemStatus → вимір тривалості попереднього рівня через Stopwatch (QPC).
/// Порт відкривається в overlapped-режимі (як у CarTester): очікування події можна перервати
/// подією зупинки, навіть коли сигналу на DSR немає. Синхронний дескриптор тут не годиться —
/// Windows серіалізує виклики на ньому, і Stop зависав би на заблокованому WaitCommEvent.
/// </summary>
public sealed class ComLineReader : IEdgeSource
{
    private readonly string _port;
    private readonly bool _invert;
    private readonly bool _dtr;
    private readonly bool _rts;
    private readonly ManualResetEvent _stopEvent = new(false);
    private SafeFileHandle? _handle;
    private Thread? _thread;
    private bool _disposed;

    public event Action<Edge>? EdgeReceived;
    public event Action<string>? Error;
    public bool IsRunning => _thread is { IsAlive: true };

    public ComLineReader(string port, bool invert, bool dtr, bool rts)
    {
        _port = port;
        _invert = invert;
        _dtr = dtr;
        _rts = rts;
    }

    public void Start()
    {
        if (IsRunning) return;
        var h = CreateFile(@"\\.\" + _port, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) ThrowLast($"Cannot open {_port}");

        var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
        if (!GetCommState(h, ref dcb)) { h.Dispose(); ThrowLast("GetCommState"); }
        dcb.BaudRate = 9600;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        // fBinary=1; fDtrControl (біти 4-5), fRtsControl (біти 12-13): 1 = ENABLE (лінія утримується активною — живлення адаптера)
        uint flags = 1u;
        if (_dtr) flags |= 1u << 4;
        if (_rts) flags |= 1u << 12;
        dcb.Flags = flags;
        if (!SetCommState(h, ref dcb)) { h.Dispose(); ThrowLast("SetCommState"); }
        EscapeCommFunction(h, _dtr ? SETDTR : CLRDTR);
        EscapeCommFunction(h, _rts ? SETRTS : CLRRTS);
        PurgeComm(h, PURGE_ALL);
        if (!SetCommMask(h, EV_DSR)) { h.Dispose(); ThrowLast("SetCommMask"); }

        _handle = h;
        _stopEvent.Reset();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "COM line reader" };
        _thread.Start();
    }

    private void Loop()
    {
        var h = _handle!;
        long last = 0;
        double tickMs = 1000.0 / Stopwatch.Frequency;
        using var ioEvent = new ManualResetEvent(false);
        var waits = new WaitHandle[] { ioEvent, _stopEvent };
        // OVERLAPPED і маска мають жити в незмінному місці пам'яті, поки операція не завершилась
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        IntPtr mask = Marshal.AllocHGlobal(sizeof(uint));
        bool pending = false;
        try
        {
            while (!_stopEvent.WaitOne(0))
            {
                ioEvent.Reset();
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = ioEvent.SafeWaitHandle.DangerousGetHandle() }, ov, false);
                Marshal.WriteInt32(mask, 0);

                if (!WaitCommEvent(h, mask, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) { Error?.Invoke($"WaitCommEvent error {err}"); break; }
                    pending = true;
                    if (WaitHandle.WaitAny(waits) == 1) break; // Stop
                    pending = false;
                    if (!GetOverlappedResult(h, ov, out _, false))
                    {
                        int e2 = Marshal.GetLastWin32Error();
                        if (e2 == ERROR_OPERATION_ABORTED) break;
                        Error?.Invoke($"WaitCommEvent error {e2}");
                        break;
                    }
                }
                long now = Stopwatch.GetTimestamp();
                if (((uint)Marshal.ReadInt32(mask) & EV_DSR) == 0) continue;
                GetCommModemStatus(h, out uint st);
                bool on = (st & MS_DSR_ON) != 0;
                // Подія = зміна рівня. Відрізок, що щойно закінчився, мав протилежний стан.
                // Оригінал: "лінія тепер ON → записати 0", тобто рівень відрізка = 1, якщо він був ON.
                int level = on ? 0 : 1;
                if (_invert) level ^= 1;
                if (last != 0)
                    EdgeReceived?.Invoke(new Edge(level, (now - last) * tickMs));
                last = now;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
        finally
        {
            if (pending)
            {
                // Скасувати незавершене очікування і дочекатися, поки ядро відпустить буфери
                CancelIoEx(h, ov);
                GetOverlappedResult(h, ov, out _, true);
            }
            Marshal.FreeHGlobal(mask);
            Marshal.FreeHGlobal(ov);
        }
    }

    public void Stop()
    {
        if (_disposed) return;
        _stopEvent.Set();
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(2000);
        _handle?.Dispose();
        _handle = null;
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _stopEvent.Dispose();
    }
}
