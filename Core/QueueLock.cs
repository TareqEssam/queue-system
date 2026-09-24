namespace QueueSystem.Core;

/// <summary>
/// قفل عام واحد لكل عمليات الطابور والتسجيل (Singleton).
/// يمنع سباق NEXT/SKIP/TRANSFER/Register بين الطلبات المتزامنة.
/// </summary>
public sealed class QueueLock : IDisposable
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_sem);
    }

    public void Dispose() => _sem.Dispose();

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _s;
        public Releaser(SemaphoreSlim s) => _s = s;
        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _s, null);
            s?.Release();
        }
    }
}
