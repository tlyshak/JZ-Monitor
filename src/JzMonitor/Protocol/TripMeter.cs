namespace JzMonitor.Protocol;

/// <summary>Накопичені підсумки поїздки після певного кадру.</summary>
public readonly record struct TripPoint(double FuelL, double DistanceKm, double EngineSeconds)
{
    /// <summary>Середня витрата, л/100км; null, поки відстань замала для осмисленого числа.</summary>
    public double? AverageL100 => DistanceKm >= 0.1 ? FuelL / DistanceKm * 100 : null;
}

/// <summary>
/// Витрата палива, відстань і час роботи двигуна за поїздку. Інтегрує по кадрах: значення попереднього
/// кадру × час до наступного (у .ctr час кадрів — цілі секунди, у живому записі — з мілісекундами).
/// Паузи довші за <see cref="MaxGapSeconds"/> (зупинка запису) не рахуються.
/// </summary>
public sealed class TripMeter
{
    public const double MaxGapSeconds = 5;

    private readonly List<TripPoint> _cumulative = new();
    private DateTime? _prevTime;
    private double _prevLph, _prevKmh;
    private bool _prevRunning;

    public void Reset()
    {
        _cumulative.Clear();
        _prevTime = null;
        _prevLph = _prevKmh = 0;
        _prevRunning = false;
    }

    /// <summary>Додати наступний кадр запису (у порядку часу).</summary>
    public void Add(ObdFrame f, DecoderSettings s)
    {
        var acc = _cumulative.Count > 0 ? _cumulative[^1] : default;
        double fuel = acc.FuelL, dist = acc.DistanceKm, eng = acc.EngineSeconds;
        if (_prevTime is DateTime pt)
        {
            double dt = (f.Time - pt).TotalSeconds;
            if (dt > 0 && dt <= MaxGapSeconds)
            {
                fuel += _prevLph * dt / 3600;
                dist += _prevKmh * dt / 3600;
                if (_prevRunning) eng += dt;
            }
        }
        _cumulative.Add(new TripPoint(fuel, dist, eng));

        _prevTime = f.Time;
        _prevLph = ParameterDecoder.FuelLph(f, s) ?? 0;
        _prevKmh = ParameterDecoder.SpeedKmh(f, s) ?? 0;
        _prevRunning = f.B(4) is > 0;
    }

    /// <summary>Підсумок на момент кадру з індексом index (включно).</summary>
    public TripPoint At(int index) =>
        index >= 0 && index < _cumulative.Count ? _cumulative[index] : default;
}
