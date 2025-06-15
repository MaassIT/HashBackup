namespace HashBackup.Utils;

/// <summary>
/// Implementiert einen Datei-basierten Lock-Mechanismus, um sicherzustellen, dass
/// das Backup-Programm nur einmal gleichzeitig läuft.
/// </summary>
public class FileLock : IDisposable
{
    private FileStream? _lockFileStream;
    private readonly string _lockFilePath;
    
    /// <summary>
    /// Erstellt eine neue Instanz des FileLock.
    /// </summary>
    /// <param name="lockFilePath">Der Pfad zur Lockdatei</param>
    public FileLock(string lockFilePath)
    {
        _lockFilePath = lockFilePath;
    }
    
    /// <summary>
    /// Versucht, den Lock zu erwerben.
    /// </summary>
    /// <returns>True wenn der Lock erworben wurde, False wenn nicht.</returns>
    public bool TryAcquireLock()
    {
        try
        {
            // Stelle sicher, dass der Verzeichnispfad existiert
            var directory = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Versuche die Datei exklusiv zu öffnen
            _lockFileStream = new FileStream(
                _lockFilePath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None
            );
            
            // Schreibe PID und Zeitstempel in die Lockdatei
            var processId = Environment.ProcessId;
            var timestamp = DateTime.Now;
            var info = $"Process ID: {processId}\nTimestamp: {timestamp:yyyy-MM-dd HH:mm:ss}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(info);
            _lockFileStream.Write(bytes, 0, bytes.Length);
            _lockFileStream.Flush();
            
            return true;
        }
        catch (IOException)
        {
            // Ein anderer Prozess hat die Datei bereits geöffnet
            // oder wir haben keine Berechtigung
            return false;
        }
    }
    
    /// <summary>
    /// Gibt den Lock frei und löscht die Lockdatei.
    /// </summary>
    private void ReleaseLock()
    {
        _lockFileStream?.Close();
        _lockFileStream?.Dispose();
        _lockFileStream = null;
        
        try
        {
            if (File.Exists(_lockFilePath))
            {
                File.Delete(_lockFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fehler beim Löschen der Lockdatei {LockFilePath}", _lockFilePath);
        }
    }
    
    /// <summary>
    /// Gibt den Lock und zugehörige Ressourcen frei.
    /// </summary>
    public void Dispose()
    {
        ReleaseLock();
        GC.SuppressFinalize(this);
    }
}
