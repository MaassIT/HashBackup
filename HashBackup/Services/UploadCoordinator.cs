namespace HashBackup.Services;

/// <summary>
/// Koordiniert den Upload von Dateien zum Storage-Backend
/// </summary>
public class UploadCoordinator
{
    private readonly IStorageBackend _backend;
    private readonly int _parallelUploads;
    private readonly int _maxRetries;
    private readonly int _retryDelay;
    private readonly bool _dryRun;
    private readonly string _jobName;
    
    // Zähler für die Upload-Statistik
    private int _savedFiles;
    private long _savedSize;
    
    public UploadCoordinator(
        IStorageBackend backend,
        int parallelUploads,
        int maxRetries,
        int retryDelay,
        bool dryRun,
        string jobName)
    {
        _backend = backend;
        _parallelUploads = parallelUploads;
        _maxRetries = maxRetries;
        _retryDelay = retryDelay;
        _dryRun = dryRun;
        _jobName = jobName;
    }
    
    /// <summary>
    /// Führt den Upload von Dateien zum Backend durch
    /// </summary>
    public async Task UploadFilesAsync(
        ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)> uploadQueue,
        CancellationToken ct = default)
    {
        if (_dryRun)
        {
            // Im Dry-Run nur die Dateien anzeigen, die hochgeladen würden
            while (uploadQueue.TryDequeue(out var item))
            {
                Log.Information("[DRY RUN] Würde Datei {FilePath} hochladen nach {DestPath}", item.filePath, item.destPath);
            }
            return;
        }
        
        _savedFiles = 0;
        _savedSize = 0;
        
        // Parallele Uploads
        var tasks = new List<Task>();
        var cts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        
        for (var i = 0; i < _parallelUploads; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!linkedCts.Token.IsCancellationRequested && uploadQueue.TryDequeue(out var item))
                {
                    try
                    {
                        var success = await _backend.UploadToDestinationAsync(item.filePath, item.destPath, item.fileHash, false, linkedCts.Token);
                        if (success)
                        {
                            // Bei Erfolg den Backup-Zeitstempel aktualisieren
                            FileAttributesUtil.SetAttribute(
                                item.filePath, 
                                $"user.{_jobName}_backup_mtime", 
                                new FileInfo(item.filePath).LastWriteTimeUtc.ToFileTimeUtc().ToString());
                            
                            // Statistik aktualisieren
                            Interlocked.Increment(ref _savedFiles);
                            Interlocked.Add(ref _savedSize, new FileInfo(item.filePath).Length);
                            
                            // Bei jedem 10. erfolgreichen Upload eine Statusmeldung ausgeben
                            if (_savedFiles % 10 == 0)
                            {
                                Log.Information(
                                    "Upload-Fortschritt: {SavedFiles} Dateien ({SavedSizeMB:F2} MB)", 
                                    _savedFiles, 
                                    (float)_savedSize / 1024 / 1024.0);
                            }
                        }
                        else if (item.tryCount < _maxRetries)
                        {
                            // Bei Misserfolg eine Wiederholung einreihen
                            await Task.Delay(_retryDelay * 1000, linkedCts.Token);
                            uploadQueue.Enqueue((item.filePath, item.destPath, item.fileHash, item.tryCount + 1));
                            Log.Warning("Upload fehlgeschlagen für {FilePath}, Versuch {TryCount}/{MaxRetries}", 
                                item.filePath, item.tryCount + 1, _maxRetries);
                        }
                        else
                        {
                            Log.Error("Datei {FilePath} konnte nicht hochgeladen werden. Maximale Anzahl an Versuchen erreicht.", item.filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Fehler beim Upload von {FilePath}", item.filePath);
                    }
                }
            }, linkedCts.Token));
        }
        
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Upload-Prozess wurde abgebrochen");
        }
        
        Log.Information("Backup abgeschlossen: {SavedFiles} Dateien gesichert mit {SavedSizeMB:F2} MB", 
            _savedFiles, (float)_savedSize / 1024 / 1024.0);
    }
    
    /// <summary>
    /// Liefert die Upload-Statistik
    /// </summary>
    public (int SavedFiles, long SavedSize) GetUploadStatistics()
    {
        return (_savedFiles, _savedSize);
    }
}
