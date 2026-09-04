namespace Impeller.App.ViewModels;

/// <summary>
/// Sends the latest value at most once per interval, and always sends the last one.
/// </summary>
/// <remarks>
/// <para>
/// A slider raises a change per pixel of a drag. Each of those used to be a call across the pipe to
/// a service other programs are also talking to, unthrottled and unordered — and the engine reads a
/// requested duty once per tick, so every write between two ticks except the last is wasted by
/// construction.
/// </para>
/// <para>
/// <strong>Throttle, not debounce.</strong> A debounce fires only once the input goes quiet, which
/// for a slider driving a physical fan means the fan does nothing during a slow drag and then jumps
/// at the end. This sends the first value immediately, then at most one per interval while the
/// value keeps moving, and the last one always lands — so the fan follows the drag and finishes
/// exactly where the user let go.
/// </para>
/// <para>
/// <strong>One request in flight.</strong> The send is awaited before the next begins. Without that,
/// two writes can complete out of order and leave the fan holding the older value, which no amount
/// of rate limiting would fix.
/// </para>
/// <para>
/// Built on <see cref="TimeProvider"/> rather than a UI timer, because this project deliberately
/// has no UI-framework reference — and because a clock that can be advanced by a test is the only
/// way to check any of the above without sleeping.
/// </para>
/// </remarks>
public sealed class ThrottledWriter<T> : IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Func<T, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private T? _pending;
    private bool _hasPending;
    private bool _running;
    private long _lastSentAt;
    private Task _pump = Task.CompletedTask;

    /// <summary>Builds a writer over a send operation.</summary>
    /// <param name="timeProvider">The clock the interval is measured on.</param>
    /// <param name="interval">The shortest gap between two sends.</param>
    /// <param name="send">What to do with a value. Must not throw for ordinary failures.</param>
    public ThrottledWriter(TimeProvider timeProvider, TimeSpan interval, Func<T, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval.Ticks);

        _time = timeProvider;
        _interval = interval;
        _send = send;

        // Far enough in the past that the very first value is never made to wait.
        _lastSentAt = timeProvider.GetTimestamp() - (long)(interval.TotalSeconds * timeProvider.TimestampFrequency);
    }

    /// <summary>How many sends have actually happened, for tests and diagnostics.</summary>
    public int Sends { get; private set; }

    /// <summary>Offers a value. Returns immediately; the send happens on its own.</summary>
    public void Write(T value)
    {
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            _pending = value;
            _hasPending = true;

            if (_running)
            {
                // A pump is already going round; it will pick this up on its next turn, and
                // anything it would have sent instead is superseded by this.
                return;
            }

            _running = true;
            _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        }
    }

    /// <summary>Waits for everything offered so far to have been sent.</summary>
    /// <remarks>For tests, and for a caller that wants to know the fan has actually been told.</remarks>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _pump;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task pump;

        lock (_gate)
        {
            _hasPending = false;
            pump = _pump;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _shutdown.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            T value;

            lock (_gate)
            {
                // Nothing left to send, so the pump finishes here rather than sitting out a
                // cooldown nobody is waiting on. That is what lets DrainAsync mean something, and
                // it leaves no timer running on an idle slider.
                if (!_hasPending || cancellationToken.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }

                value = _pending!;
                _hasPending = false;
            }

            // The wait is before the send and only as long as is actually owed, so the rate limit
            // survives the pump stopping and starting - which it does every time a drag pauses.
            var owed = _interval - _time.GetElapsedTime(_lastSentAt);

            if (owed > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(owed, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        _running = false;
                    }

                    return;
                }
            }

            lock (_gate)
            {
                _lastSentAt = _time.GetTimestamp();
                Sends++;
            }

            try
            {
                await _send(value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed write is the caller's business, not this class's. Stopping the pump here
                // would mean one dropped connection silently disabled the slider for good.
            }
        }
    }
}
