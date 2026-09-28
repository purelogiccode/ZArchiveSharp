namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Cooperative pause gate: workers block in
/// <see cref="PauseToken.WaitIfPaused"/> while paused and keep honoring
/// cancellation. <see cref="PauseToken"/> is a snapshot struct; the default
/// value never pauses. Dispose after the workers stop to release the
/// underlying wait handle.
/// </summary>
public sealed class PauseTokenSource : IDisposable
{
    private readonly ManualResetEventSlim _running = new(initialState: true);
    private bool _disposed;

    /// <summary>True while paused.</summary>
    public bool IsPaused => !_running.IsSet;

    /// <summary>Token observing this source.</summary>
    public PauseToken Token => new(_running);

    /// <summary>Pauses workers at their next gate check.</summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _running.Reset();
    }

    /// <summary>Resumes paused workers.</summary>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _running.Set();
    }

    /// <summary>
    /// Releases the underlying <see cref="ManualResetEventSlim"/> (which may
    /// own an OS wait handle). Tokens observing this source must not be
    /// waited on afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running.Dispose();
    }
}
