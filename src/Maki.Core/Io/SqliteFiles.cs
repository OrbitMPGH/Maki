namespace Maki.Core.Io;

/// <summary>
/// File-level care for SQLite databases on Docker Desktop's virtiofs bind mounts, where SQLite's
/// shared-memory file (<c>-shm</c>) is mapped into memory and two things kill the process with
/// SIGBUS.
/// <para>
/// A <c>-shm</c> left by a process that did not close cleanly: the next process to open the database
/// truncates and regrows that file before mapping it, and on virtiofs the regrown mapping faults.
/// <see cref="DeleteStaleSharedMemory"/> removes it first. SQLite rebuilds the index from the
/// <c>-wal</c> on the next open, which is all the truncation achieves anyway; the <c>-wal</c> holds
/// committed data and is never touched.
/// </para>
/// <para>
/// A file created inside the container reports a provisional inode number until its size is set or
/// a sync or rename asks the host. SQLite tells files apart by inode, so connections opened on
/// either side of the switch treat one database as two, and the later one truncates the
/// <c>-shm</c> the earlier ones have mapped. <see cref="CreateIfMissing"/> settles the number before
/// SQLite ever sees the file.
/// </para>
/// </summary>
public static class SqliteFiles
{
    // SQLite's unix VFS (UNIX_SHM_DMS): every process with the database open in WAL mode holds a
    // read lock on this byte of the -shm file, and a process takes the write lock only when it is
    // the first to attach.
    private const long AttachLockOffset = 128;

    /// <summary>
    /// Deletes every <c>*-shm</c> in <paramref name="directory"/> that no process is attached to,
    /// and returns the paths it deleted.
    /// <para>
    /// Call it only before this process opens any database in the directory. Closing the handle
    /// used for the check drops every POSIX lock this process holds on the file, SQLite's own
    /// included, and a process never conflicts with its own locks, so a live <c>-shm</c> of ours
    /// would look abandoned.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> DeleteStaleSharedMemory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var deleted = new List<string>();
        foreach (var shm in Directory.EnumerateFiles(directory, "*-shm"))
        {
            if (TryDeleteStaleSharedMemory(shm))
            {
                deleted.Add(shm);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes <paramref name="shmPath"/> when no process holds SQLite's attach lock on it. False
    /// when it is missing, in use, or cannot be deleted. Same precondition as
    /// <see cref="DeleteStaleSharedMemory"/>.
    /// </summary>
    public static bool TryDeleteStaleSharedMemory(string shmPath)
    {
        if (OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                shmPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(AttachLockOffset, 1);
            File.Delete(shmPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when no other process has <paramref name="databasePath"/> open: it has no <c>-shm</c>, or
    /// nothing holds SQLite's attach lock on it. False when that cannot be told (macOS, an unreadable
    /// file). Same precondition as <see cref="DeleteStaleSharedMemory"/>.
    /// </summary>
    public static bool IsUnattached(string databasePath)
    {
        var shmPath = databasePath + "-shm";
        if (!File.Exists(shmPath))
        {
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                shmPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            stream.Lock(AttachLockOffset, 1);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates <paramref name="databasePath"/> as an empty file, which SQLite opens as an empty
    /// database, and sets its size so the filesystem reports its final inode number. Leaves an
    /// existing file alone.
    /// </summary>
    public static void CreateIfMissing(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            return;
        }

        using var handle = File.OpenHandle(databasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        RandomAccess.SetLength(handle, RandomAccess.GetLength(handle));
    }
}
