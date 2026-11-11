namespace JzMonitor.Protocol;

/// <summary>
/// Відрізок постійного рівня на лінії: логічний рівень (1 = спокій/стоп-біт) та його тривалість.
/// Саме такі записи (рівень + тривалість) формує потік читання в оригінальному CarTester.
/// </summary>
public readonly record struct Edge(int Level, double DurationMs);

public interface IEdgeSource : IDisposable
{
    event Action<Edge>? EdgeReceived;
    event Action<string>? Error;
    void Start();
    void Stop();
    bool IsRunning { get; }
}
