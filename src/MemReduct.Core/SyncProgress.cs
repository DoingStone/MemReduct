namespace MemReduct.Core;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler inline on the reporting thread.
/// <para>
/// <see cref="Progress{T}"/> deliberately posts callbacks to a captured synchronization
/// context (or the thread pool), which makes them asynchronous: results arrive out of order
/// relative to the operation that produced them and interleave when they share a console.
/// Cleaners report one small value per area and the caller is already off the UI thread, so
/// inline delivery is both correct and simpler.
/// </para>
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Report(T value) => _handler(value);
}
