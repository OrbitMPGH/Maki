namespace Maki.Core.Entities;

public class RootFolder
{
    /// <summary>The free space new downloads into a root wait for when <see cref="MinFreeBytes"/> is unset: 5 GiB.</summary>
    public const long DefaultMinFreeBytes = 5L * 1024 * 1024 * 1024;

    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Downloads into this root are held while its volume has less free space than this. Null means
    /// <see cref="DefaultMinFreeBytes"/>, 0 means never hold.
    /// </summary>
    public long? MinFreeBytes { get; set; }

    public long EffectiveMinFreeBytes => MinFreeBytes ?? DefaultMinFreeBytes;
}
