namespace FortnitePorting.Services;

/// <summary>
/// Coordinates the hot reload of the FileProvider with in-flight API requests.
/// A reload tears the provider down and rebuilds it from the new build's manifest, so nothing may
/// read from it while that runs: new requests are rejected with 503 and the reloader first waits for
/// the requests that are already running to drain.
/// </summary>
public sealed class ProviderReloadGate
{
    /// <summary>The single gate instance (also registered in DI).</summary>
    public static ProviderReloadGate Instance { get; } = new();

    private int _inFlight;
    private volatile bool _reloading;
    private volatile string _state = "ready";
    private int _generation;

    private ProviderReloadGate() { }

    /// <summary>True while the provider is being rebuilt (requests must not touch it).</summary>
    public bool IsReloading => _reloading;

    /// <summary>"ready", or "reloading:&lt;build&gt;" while a rebuild is running.</summary>
    public string State => _state;

    /// <summary>
    /// Incremented after every completed reload. Background services compare it against the value
    /// they last saw to drop per-build state (e.g. "the key I already submitted").
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Number of requests currently working with the provider.</summary>
    public int InFlightRequests => Volatile.Read(ref _inFlight);

    /// <summary>When the running reload started (UTC), or null when none is running.</summary>
    public DateTime? ReloadStartedUtc { get; private set; }

    /// <summary>
    /// Registers a request as using the provider. Returns false when a reload is running — the caller
    /// must then reject the request instead of reading from the provider.
    /// </summary>
    public bool TryEnter()
    {
        if (_reloading)
        {
            return false;
        }

        Interlocked.Increment(ref _inFlight);

        // Re-check: a reload may have started between the check and the increment.
        if (_reloading)
        {
            Interlocked.Decrement(ref _inFlight);
            return false;
        }

        return true;
    }

    /// <summary>Releases a request registered with <see cref="TryEnter"/>.</summary>
    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// Closes the gate and waits (up to <paramref name="drainTimeout"/>) for running requests to finish.
    /// </summary>
    public async Task BeginReloadAsync(string state, TimeSpan drainTimeout)
    {
        _state = state;
        _reloading = true;
        ReloadStartedUtc = DateTime.UtcNow;

        var deadline = DateTime.UtcNow + drainTimeout;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Console.WriteLine($"Reload drain timed out with {InFlightRequests} request(s) still running; continuing anyway.");
                break;
            }

            await Task.Delay(200);
        }
    }

    /// <summary>Reopens the gate after a reload (successful or not) and bumps <see cref="Generation"/>.</summary>
    public void EndReload()
    {
        Interlocked.Increment(ref _generation);
        ReloadStartedUtc = null;
        _state = "ready";
        _reloading = false;
    }
}
