namespace JzMonitor.Protocol;

/// <summary>
/// Перетворює послідовність відрізків рівня (Edge) у кадри.
/// Синхронізація — по довгій паузі високого рівня між кадрами (у JZ-ECU ≈ 16 біт + 2 стоп-біти).
/// На відміну від оригіналу (поріг 163 біти → рівно 12 байтів), довжина кадру тут визначається паузою,
/// тому байти після 12-го не губляться.
/// </summary>
public sealed class FrameAssembler
{
    public double BitTimeMs { get; set; } = 8.2;
    /// <summary>Мінімальна кількість біт «1» поспіль, яку вважаємо паузою між кадрами.</summary>
    public int GapBits { get; set; } = 16;
    /// <summary>Скільки байтів має бути в кадрі, щоб вважати його повним (оригінал вимагав 12).</summary>
    public int MinDataBytes { get; set; } = 12;

    private readonly List<byte> _bits = new(2048);
    private bool _synced;
    private double _sumDur;
    private int _sumBits;
    private double _sumAbsErr;
    private double _maxAbsErr;
    private int _edgeCount;

    public event Action<ObdFrame>? FrameCompleted;

    /// <summary>Виміряна (середня) тривалість біта за останній кадр, мс.</summary>
    public double MeasuredBitMs { get; private set; }
    /// <summary>Середнє та максимальне відхилення фронтів від сітки бітів, мс (аналог «Check performance»).</summary>
    public double AvgJitterMs { get; private set; }
    public double MaxJitterMs { get; private set; }

    public void Reset()
    {
        _bits.Clear();
        _synced = false;
        ResetStats();
    }

    private void ResetStats()
    {
        _sumDur = 0; _sumBits = 0; _sumAbsErr = 0; _maxAbsErr = 0; _edgeCount = 0;
    }

    public void Push(Edge e)
    {
        int n = (int)Math.Round(e.DurationMs / BitTimeMs);
        if (e.Level == 1 && n >= GapBits)
        {
            if (_synced && _bits.Count > 0)
            {
                // Старші одиничні біти останнього байта і його стоп-біти зливаються з паузою —
                // доповнюємо одиницями до межі слова (заголовок 15 біт + слова по 11).
                if (_bits.Count < 15) while (_bits.Count < 15) _bits.Add(1);
                else while ((_bits.Count - 15) % 11 != 0) _bits.Add(1);
                Decode();
            }
            _bits.Clear();
            ResetStats();
            _synced = true;
            return;
        }
        if (!_synced) return;
        if (n < 1) n = 1;
        for (int i = 0; i < n; i++) _bits.Add((byte)e.Level);

        double err = Math.Abs(e.DurationMs - n * BitTimeMs);
        _sumAbsErr += err;
        _maxAbsErr = Math.Max(_maxAbsErr, err);
        _sumDur += e.DurationMs;
        _sumBits += n;
        _edgeCount++;

        if (_bits.Count > 4000) // втрачено синхронізацію
        {
            _bits.Clear();
            _synced = false;
        }
    }

    private void Decode()
    {
        var bits = _bits;
        var now = DateTime.Now;
        if (_sumBits > 0)
        {
            MeasuredBitMs = _sumDur / _sumBits;
            AvgJitterMs = _sumAbsErr / Math.Max(1, _edgeCount);
            MaxJitterMs = _maxAbsErr;
        }

        if (bits.Count < 15)
        {
            Emit(Fail("кадр закороткий"));
            return;
        }
        var id = bits.Skip(1).Take(12).ToArray();
        if (bits[0] != 0 || bits[13] != 1 || bits[14] != 1)
        {
            Emit(Fail("помилка заголовка", id));
            return;
        }

        var data = new List<byte>(16);
        int pos = 15;
        string? error = null;
        while (pos < bits.Count)
        {
            if (bits[pos] != 0 || bits[pos + 9] != 1 || bits[pos + 10] != 1)
            {
                error = $"помилка рамки байта {data.Count + 1}";
                break;
            }
            int v = 0;
            for (int i = 0; i < 8; i++) if (bits[pos + 1 + i] != 0) v |= 1 << i;
            data.Add((byte)v);
            pos += 11;
        }

        if (error == null && data.Count < MinDataBytes)
            error = $"лише {data.Count} байт(ів), очікувалось ≥ {MinDataBytes}";

        Emit(new ObdFrame
        {
            Time = now,
            IdBits = id,
            Bytes = data.ToArray(),
            Valid = error == null,
            Error = error
        });

        ObdFrame Fail(string why, byte[]? idBits = null) =>
            new() { Time = now, IdBits = idBits ?? new byte[12], Bytes = [], Valid = false, Error = why };
    }

    private void Emit(ObdFrame f) => FrameCompleted?.Invoke(f);

    /// <summary>Побудова бітового потоку кадру — для симулятора і тестів.</summary>
    public static List<int> BuildFrameBits(int id, IReadOnlyList<byte> data)
    {
        var bits = new List<int> { 0 };
        for (int i = 0; i < 12; i++) bits.Add((id >> i) & 1);
        bits.Add(1); bits.Add(1);
        foreach (var b in data)
        {
            bits.Add(0);
            for (int i = 0; i < 8; i++) bits.Add((b >> i) & 1);
            bits.Add(1); bits.Add(1);
        }
        return bits;
    }
}
