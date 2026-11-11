namespace JzMonitor.Protocol;

/// <summary>
/// Генератор сигналу для роботи без автомобіля: формує ті самі відрізки рівня, що й реальний ECU,
/// з невеликим джитером, і проганяє їх через звичайний декодер.
/// </summary>
public sealed class SimulatedSource : IEdgeSource
{
    private readonly double _bitMs;
    private readonly int _extraBytes;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly Random _rnd = new();

    public event Action<Edge>? EdgeReceived;
    public event Action<string>? Error { add { } remove { } }
    public bool IsRunning => _thread is { IsAlive: true };

    public SimulatedSource(double bitMs = 8.2, int extraBytes = 0)
    {
        _bitMs = bitMs;
        _extraBytes = extraBytes;
    }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Simulator" };
        _thread.Start();
    }

    private void Loop()
    {
        double t = 0;
        EmitRun(1, 20); // початкова пауза для синхронізації
        while (!_stop)
        {
            var data = MakeData(t);
            var bits = FrameAssembler.BuildFrameBits(0b1010_0000_0001, data);
            for (int i = 0; i < 16; i++) bits.Add(1); // пауза між кадрами
            int level = bits[0], run = 0;
            double frameMs = 0;
            foreach (var b in bits)
            {
                if (b == level) { run++; continue; }
                frameMs += EmitRun(level, run);
                level = b; run = 1;
            }
            // останній відрізок (стоп + пауза) завершиться стартовим бітом наступного кадру
            frameMs += EmitRun(level, run);
            t += frameMs / 1000.0;
            Thread.Sleep((int)frameMs);
        }
    }

    private double EmitRun(int level, int bits)
    {
        double ms = bits * _bitMs + (_rnd.NextDouble() - 0.5) * 1.0;
        EdgeReceived?.Invoke(new Edge(level, ms));
        return ms;
    }

    private byte[] MakeData(double t)
    {
        double warm = Math.Min(1.0, t / 120.0);
        double rpm = 750 + 250 * Math.Sin(t / 7) + (t % 40 > 30 ? 1500 : 0);
        var d = new byte[12 + _extraBytes];
        d[0] = (byte)Math.Clamp(24 + 8 * Math.Sin(t / 5), 0, 255);      // впорскування ×0.125 мс
        d[1] = (byte)Math.Clamp(85 + 6 * Math.Sin(t / 3), 0, 255);      // кут ×0.47−30
        d[2] = (byte)Math.Clamp(70 - 30 * warm, 0, 255);                 // IAC
        d[3] = (byte)Math.Clamp(rpm / 25, 0, 255);                       // оберти
        d[4] = (byte)Math.Clamp(35 + 10 * Math.Sin(t / 4), 0, 255);     // MAP
        d[5] = (byte)Math.Clamp(110 + 115 * warm, 0, 255);               // ОР
        d[6] = (byte)(t % 40 > 30 ? 40 : 0);                             // дросель
        d[7] = 0;                                                        // швидкість
        d[8] = (byte)(warm > 0.8 ? 128 + 40 * Math.Sin(t) : 0);
        d[9] = (byte)(warm > 0.8 ? 128 + 40 * Math.Cos(t) : 0);
        int b11 = (t < 15 ? 1 : 0) | (warm < 0.5 ? 2 : 0) | (warm > 0.8 ? 0x20 : 0);
        int b12 = (t % 40 > 30 ? 0 : 2) | 8 | (Math.Sin(t) > 0 ? 0x10 : 0) | 0x80;
        d[10] = (byte)b11;
        d[11] = (byte)b12;
        for (int i = 12; i < d.Length; i++) d[i] = (byte)_rnd.Next(256);
        return d;
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();
}
