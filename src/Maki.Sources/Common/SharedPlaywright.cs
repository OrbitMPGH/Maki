using Microsoft.Playwright;

namespace Maki.Sources.Common;

/// <summary>
/// One Playwright driver for every headless browser. Each <see cref="IPlaywright"/> is its own
/// Node process (~96 MB anonymous on a NAS), so MangaFire and TopManhua running at once used to
/// hold two. Reference counted: the driver starts with the first browser and ends with the last,
/// which keeps the idle shutdown's saving intact when only one of them is in use.
/// </summary>
public sealed class SharedPlaywright : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPlaywright? _current;
    private int _holders;

    public async Task<IPlaywright> AcquireAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _current ??= await Playwright.CreateAsync();
            _holders++;
            return _current;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Ends the driver once no browser holds it. A driver already discarded is a no-op.</summary>
    public async Task ReleaseAsync(IPlaywright playwright)
    {
        await _lock.WaitAsync();
        try
        {
            if (!ReferenceEquals(playwright, _current) || --_holders > 0)
            {
                return;
            }

            _current = null;
            playwright.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Drops a driver a launch failed on, so the next acquire starts a fresh one rather than every
    /// browser relaunching against a dead process. Other holders' later releases become no-ops.
    /// </summary>
    public async Task DiscardAsync(IPlaywright playwright)
    {
        await _lock.WaitAsync();
        try
        {
            if (!ReferenceEquals(playwright, _current))
            {
                return;
            }

            _current = null;
            _holders = 0;
            playwright.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
    }
}
