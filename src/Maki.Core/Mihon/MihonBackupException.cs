namespace Maki.Core.Mihon;

public enum MihonBackupFailure
{
    NotABackup,
    JsonBackup,
    TooLarge,
    Corrupt,
}

public sealed class MihonBackupException(MihonBackupFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public MihonBackupFailure Reason { get; } = reason;
}
