using JzMonitor.Protocol;

namespace JzMonitor.Tests.Protocol;

/// <summary>Формує відрізки рівня так, як їх видає ECU, і проганяє через FrameAssembler.</summary>
internal static class SignalBuilder
{
    public const double BitMs = 8.2;

    public static List<ObdFrame> Decode(IEnumerable<(int Id, byte[] Data)> frames, double jitterMs, int gapBits = 16, int seed = 1)
    {
        var rnd = new Random(seed);
        var asm = new FrameAssembler();
        var got = new List<ObdFrame>();
        asm.FrameCompleted += got.Add;

        void Run(int level, int bits) =>
            asm.Push(new Edge(level, bits * BitMs + (rnd.NextDouble() - 0.5) * 2 * jitterMs));

        Run(1, 25); // пауза для синхронізації
        foreach (var (id, data) in frames)
        {
            var bits = FrameAssembler.BuildFrameBits(id, data);
            for (int i = 0; i < gapBits; i++) bits.Add(1);
            int level = bits[0], run = 0;
            foreach (var b in bits)
            {
                if (b == level) { run++; continue; }
                Run(level, run);
                level = b;
                run = 1;
            }
            Run(level, run);
        }
        asm.Push(new Edge(0, BitMs)); // стартовий біт наступного кадру завершує паузу
        return got;
    }
}
