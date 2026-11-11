using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using static JzMonitor.Protocol.NativeComm;

namespace JzMonitor.Protocol;

public enum InputLine { Dsr, Cts }

/// <summary>
/// Читає діагностичний сигнал ECU через лінію стану модему (DSR за замовчуванням), як і оригінал:
/// WaitCommEvent(EV_DSR) → GetCommModemStatus → вимір тривалості попереднього рівня через Stopwatch (QPC).
/// </summary>
public sealed class ComLineReader : IEdgeSource
{
    private readonly string _port;
    private readonly InputLine _line;
    private readonly bool _invert;
    private readonly bool _dtr;
    private readonly bool _rts;
    private SafeFileHandle? _handle;
    private Thread? _thread;
    private volatile bool _stop;

    public event Action<Edge>? EdgeReceived;
    public event Action<string>? Error;
    public bool IsRunning => _thread is { IsAlive: true };

    public ComLineReader(string port, InputLine line, bool invert, bool dtr, bool rts)
    {
        _port = port;
        _line = line;
        _invert = invert;
        _dtr = dtr;
        _rts = rts;
    }

    public void Start()
    {
        if (IsRunning) return;
        var h = CreateFile(@"\\.\" + _port, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) ThrowLast($"Не вдалося відкрити {_port}");

        var dcb = new DCB { DCBlength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DCB>() };
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
        if (!SetCommMask(h, _line == InputLine.Dsr ? EV_DSR : EV_CTS)) { h.Dispose(); ThrowLast("SetCommMask"); }

        _handle = h;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "COM line reader" };
        _thread.Start();
    }

    private void Loop()
    {
        var h = _handle!;
        uint evMask = _line == InputLine.Dsr ? EV_DSR : EV_CTS;
        uint msMask = _line == InputLine.Dsr ? MS_DSR_ON : MS_CTS_ON;
        long last = 0;
        double tickMs = 1000.0 / Stopwatch.Frequency;
        try
        {
            while (!_stop)
            {
                if (!WaitCommEvent(h, out uint ev, IntPtr.Zero))
                {
                    if (_stop) break;
                    Error?.Invoke($"WaitCommEvent: помилка {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                    break;
                }
                long now = Stopwatch.GetTimestamp();
                if (_stop) break;
                if ((ev & evMask) == 0) continue; // SetCommMask(0) при зупинці теж повертає сюди
                GetCommModemStatus(h, out uint st);
                bool on = (st & msMask) != 0;
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
    }

    public void Stop()
    {
        _stop = true;
        if (_handle is { IsInvalid: false } h)
        {
            SetCommMask(h, 0); // змушує WaitCommEvent повернутися
            _thread?.Join(500);
            h.Dispose();
        }
        _handle = null;
        _thread = null;
    }

    public void Dispose() => Stop();
}
