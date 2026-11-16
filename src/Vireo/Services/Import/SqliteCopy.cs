using System.IO;
using Microsoft.Data.Sqlite;

namespace Vireo.Services.Import;

/// <summary>
/// Opens another browser's database without disturbing it: the file (and its journal) is copied first and the copy is read.
/// A running browser keeps its files open, so the copy is made with sharing allowed; if even that is refused the person
/// is told to close the other browser.
/// </summary>
public sealed class SqliteCopy : IDisposable
{
    private static readonly object Init = new();
    private static bool _ready;
    private readonly string _folder;
    public SqliteConnection Connection { get; }

    public SqliteCopy(string databasePath)
    {
        lock (Init)
        {
            if (!_ready) { SQLitePCL.Batteries_V2.Init(); _ready = true; }
        }
        _folder = Path.Combine(Path.GetTempPath(), "vireo-import-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_folder);
        var copy = Path.Combine(_folder, Path.GetFileName(databasePath));
        try
        {
            CopyShared(databasePath, copy);
            foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
                if (File.Exists(databasePath + suffix)) CopyShared(databasePath + suffix, copy + suffix);
        }
        catch (IOException)
        {
            Cleanup();
            throw new ImportException($"{Path.GetFileName(databasePath)} is in use. Close that browser completely (also in the tray) and try again.");
        }
        Connection = new SqliteConnection($"Data Source={copy};Pooling=False");
        Connection.Open();
    }

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write);
        source.CopyTo(target);
    }

    private void Cleanup()
    {
        try { Directory.Delete(_folder, true); } catch { /* still closing */ }
    }

    public void Dispose()
    {
        Connection?.Dispose();
        SqliteConnection.ClearAllPools();
        Cleanup();
    }
}

public sealed class ImportException(string message) : Exception(message);
