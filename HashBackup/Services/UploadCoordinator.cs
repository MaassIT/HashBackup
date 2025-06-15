namespace HashBackup.Services;

/// <summary>
/// Koordiniert den Upload von Dateien zum Storage-Backend
/// </summary>
public class UploadCoordinator(
    IStorageBackend backend,
    int parallelUploads,
    int maxRetries,
    int retryDelay,
    bool dryRun,
    string jobName)
{
    // Zähler für die Upload-Statistik
    private int _savedFiles;
    private long _savedSize;
    
    // Gesamtstatistik für den Fortschritt
    private int _totalFiles;
    private long _totalSize;
    private DateTime _startTime;
    private const int ReportInterval = 10; // Meldung alle 10 Dateien

    /// <summary>
    /// Führt den Upload von Dateien zum Backend durch
    /// </summary>
    public async Task UploadFilesAsync(
        ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)> uploadQueue,
        CancellationToken ct = default)
    {
        if (dryRun)
        {
            // Im Dry-Run nur die Dateien anzeigen, die hochgeladen würden
            while (uploadQueue.TryDequeue(out var item))
            {
                Log.Information("[DRY RUN] Würde Datei {FilePath} hochladen nach {DestPath}", item.filePath, item.destPath);
            }
            return;
        }
        
        // Initialisiere Statistik
        _savedFiles = 0;
        _savedSize = 0;
        _startTime = DateTime.Now;
        
        // Berechne Gesamtstatistik für Fortschrittsanzeige
        CalculateTotalStatistics(uploadQueue);
        
        Log.Information("Starte Upload von {TotalFiles} Dateien mit insgesamt {TotalSizeMB:F2} MB", 
            _totalFiles, (float)_totalSize / 1024 / 1024.0);
        
        // Parallele Uploads
        var tasks = new List<Task>();
        var cts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        
        for (var i = 0; i < parallelUploads; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (linkedCts is { Token.IsCancellationRequested: false } && uploadQueue.TryDequeue(out var item))
                {
                    try
                    {
                        var success = await backend.UploadToDestinationAsync(item.filePath, item.destPath, item.fileHash, false, linkedCts.Token);
                        if (success)
                        {
                            // Bei Erfolg den Backup-Zeitstempel aktualisieren
                            FileAttributesUtil.SetAttribute(
                                item.filePath, 
                                $"user.{jobName}_backup_mtime", 
                                new FileInfo(item.filePath).LastWriteTimeUtc.ToFileTimeUtc().ToString());
                            
                            var fileSize = new FileInfo(item.filePath).Length;
                            
                            // Statistik aktualisieren
                            Interlocked.Increment(ref _savedFiles);
                            Interlocked.Add(ref _savedSize, fileSize);
                            
                            // Fortschrittsanzeige aktualisieren
                            if (_savedFiles % ReportInterval == 0 || _savedFiles == _totalFiles)
                            {
                                ReportProgress();
                            }
                        }
                        else if (item.tryCount < maxRetries)
                        {
                            // Bei Misserfolg eine Wiederholung einreihen
                            await Task.Delay(retryDelay * 1000, linkedCts.Token);
                            uploadQueue.Enqueue((item.filePath, item.destPath, item.fileHash, item.tryCount + 1));
                            Log.Warning("Upload fehlgeschlagen für {FilePath}, Versuch {TryCount}/{MaxRetries}", 
                                item.filePath, item.tryCount + 1, maxRetries);
                        }
                        else
                        {
                            Log.Error("Datei {FilePath} konnte nicht hochgeladen werden. Maximale Anzahl an Versuchen erreicht", item.filePath);
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
        
        // Abschließende Statusmeldung
        var duration = DateTime.Now - _startTime;
        var uploadSpeedMBps = _savedSize > 0 ? (_savedSize / 1024.0 / 1024.0) / duration.TotalSeconds : 0;
        
        Log.Information("Backup abgeschlossen: {SavedFiles} von {TotalFiles} Dateien gesichert mit {SavedSizeMB:F2} MB in {Duration:hh\\:mm\\:ss} (durchschnittlich {SpeedMBps:F2} MB/s)", 
            _savedFiles, 
            _totalFiles,
            (float)_savedSize / 1024 / 1024.0,
            duration,
            uploadSpeedMBps);
    }
    
    /// <summary>
    /// Berechnet die Gesamtstatistik für die Fortschrittsanzeige
    /// </summary>
    private void CalculateTotalStatistics(ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)> queue)
    {
        _totalFiles = queue.Count;
        _totalSize = 0;
        
        // Berechne die Gesamtgröße aller Dateien
        foreach (var (filePath, _, _, _) in queue)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                _totalSize += fileInfo.Length;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Zugriff auf {FilePath} für die Gesamtstatistik", filePath);
            }
        }
    }
    
    /// <summary>
    /// Gibt einen detaillierten Fortschrittsbericht aus
    /// </summary>
    private void ReportProgress()
    {
        // Prozentsatz des Fortschritts
        var percentFiles = _totalFiles > 0 ? (float)_savedFiles / _totalFiles * 100 : 0;
        var percentSize = _totalSize > 0 ? (float)_savedSize / _totalSize * 100 : 0;
        
        // Berechne verbleibende Anzahl und Größe
        var remainingFiles = _totalFiles - _savedFiles;
        var remainingSize = _totalSize - _savedSize;
        
        // Berechne geschätzte verbleibende Zeit
        var elapsedTime = DateTime.Now - _startTime;
        var remainingTime = TimeSpan.Zero;
        
        if (_savedSize > 0 && _totalSize > 0)
        {
            var completionFraction = (double)_savedSize / _totalSize;
            if (completionFraction > 0)
            {
                var estimatedTotalTime = TimeSpan.FromSeconds(elapsedTime.TotalSeconds / completionFraction);
                remainingTime = estimatedTotalTime - elapsedTime;
            }
        }
        
        // Berechne aktuelle Upload-Geschwindigkeit
        var uploadSpeedMBps = elapsedTime.TotalSeconds > 0 ? (_savedSize / 1024.0 / 1024.0) / elapsedTime.TotalSeconds : 0;
        
        Log.Information(
            "Upload-Fortschritt: {SavedFiles}/{TotalFiles} Dateien ({PercentFiles:F1}%) - {SavedSizeMB:F2}/{TotalSizeMB:F2} MB ({PercentSize:F1}%) - Noch {RemainingFiles} Dateien ({RemainingMB:F2} MB) - {SpeedMBps:F2} MB/s - Verbleibend: {RemainingTime:hh\\:mm\\:ss}", 
            _savedFiles, 
            _totalFiles, 
            percentFiles,
            (float)_savedSize / 1024 / 1024.0,
            (float)_totalSize / 1024 / 1024.0,
            percentSize,
            remainingFiles,
            (float)remainingSize / 1024 / 1024.0,
            uploadSpeedMBps,
            remainingTime);
    }
    
    /// <summary>
    /// Liefert die Upload-Statistik
    /// </summary>
    public (int SavedFiles, int TotalFiles, long SavedSize, long TotalSize) GetUploadStatistics()
    {
        return (_savedFiles, _totalFiles, _savedSize, _totalSize);
    }
}
