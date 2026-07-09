namespace HashBackup.Services;

/// <summary>
/// Koordiniert parallele, begrenzte Uploads und die Retry-Behandlung eines Backup-Laufs.
/// </summary>
public sealed class UploadCoordinator(
    IStorageBackend backend,
    int parallelUploads,
    int maxRetries,
    int retryDelay,
    bool dryRun,
    string jobName)
{
    private const int ReportInterval = 10;
    private int _savedFiles;
    private long _savedSize;
    private int _failedFiles;
    private int _totalFiles;
    private long _totalSize;
    private DateTime _startTime;

    public async Task UploadFilesAsync(
        ConcurrentQueue<UploadWorkItem> uploadQueue,
        CancellationToken ct = default)
    {
        if (dryRun)
        {
            while (uploadQueue.TryDequeue(out var item))
            {
                Log.Information("[DRY RUN] Würde Datei {FilePath} hochladen nach {DestPath}", item.FilePath, item.DestinationPath);
            }

            return;
        }

        _savedFiles = 0;
        _savedSize = 0;
        _failedFiles = 0;
        _startTime = DateTime.Now;
        CalculateTotalStatistics(uploadQueue);

        Log.Information(
            "Starte Upload von {TotalFiles} Dateien mit insgesamt {TotalSizeMB:F2} MB",
            _totalFiles,
            _totalSize / 1024.0 / 1024.0);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, parallelUploads)
            .Select(_ => Task.Run(
                () => UploadWorkerAsync(uploadQueue, linkedCts.Token),
                linkedCts.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Upload-Prozess wurde abgebrochen");
        }

        var duration = DateTime.Now - _startTime;
        var uploadSpeedMbPerSecond = _savedSize > 0 && duration.TotalSeconds > 0
            ? _savedSize / 1024.0 / 1024.0 / duration.TotalSeconds
            : 0;

        Log.Information(
            "Backup abgeschlossen: {SavedFiles} von {TotalFiles} Dateien gesichert mit {SavedSizeMB:F2} MB in {Duration} (durchschnittlich {SpeedMBps:F2} MB/s)",
            _savedFiles,
            _totalFiles,
            _savedSize / 1024.0 / 1024.0,
            duration,
            uploadSpeedMbPerSecond);

        if (_failedFiles > 0)
        {
            Log.Error("{FailedFiles} Dateien konnten in diesem Lauf nicht gesichert werden.", _failedFiles);
        }
    }

    public (int SavedFiles, int TotalFiles, long SavedSize, long TotalSize) GetUploadStatistics() =>
        (_savedFiles, _totalFiles, _savedSize, _totalSize);

    public int GetFailedFileCount() => _failedFiles;

    private async Task UploadWorkerAsync(
        ConcurrentQueue<UploadWorkItem> uploadQueue,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && uploadQueue.TryDequeue(out var item))
        {
            UploadResult result;

            try
            {
                result = await backend.UploadToDestinationAsync(
                    item.FilePath,
                    item.DestinationPath,
                    item.FileHash,
                    isImportant: false,
                    ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unerwarteter Fehler beim Upload von {FilePath}", item.FilePath);
                result = UploadResult.Failed;
            }

            if (result.Success)
            {
                RegisterSuccessfulUpload(item);
                continue;
            }

            if (result.SourceWasModified)
            {
                // The storage backend has verified that the source no longer matches the
                // content-addressed blob name. Retrying the stale hash would be incorrect.
                Interlocked.Increment(ref _failedFiles);
                Log.Warning(
                    "Upload von {FilePath} wird nicht mit dem veralteten Hash wiederholt; die Datei wird im nächsten vollständigen Backup-Lauf neu bewertet.",
                    item.FilePath);
                continue;
            }

            await RequeueOrFailAsync(item, uploadQueue, ct);
        }
    }

    private void RegisterSuccessfulUpload(UploadWorkItem item)
    {
        var fileInfo = new FileInfo(item.FilePath);
        fileInfo.Refresh();
        var currentMtime = FileAttributesUtil.DateTimeToUnixTimestamp(fileInfo.LastWriteTimeUtc);

        if (fileInfo.Length != item.ExpectedLength ||
            !string.Equals(currentMtime, item.HashMtime, StringComparison.Ordinal))
        {
            // The uploaded bytes were valid for the selected hash, but the source already
            // represents a newer version. Do not mark that newer version as backed up.
            Interlocked.Increment(ref _failedFiles);
            Log.Warning(
                "Quelle wurde direkt nach dem Upload geändert und bleibt für den nächsten Lauf offen: {FilePath}",
                item.FilePath);
            return;
        }

        FileAttributesUtil.SetAttribute(
            item.FilePath,
            $"user.{jobName}_backup_mtime",
            item.HashMtime);

        var savedFiles = Interlocked.Increment(ref _savedFiles);
        Interlocked.Add(ref _savedSize, item.ExpectedLength);

        if (savedFiles % ReportInterval == 0 || savedFiles == _totalFiles)
        {
            ReportProgress();
        }
    }

    private async Task RequeueOrFailAsync(
        UploadWorkItem item,
        ConcurrentQueue<UploadWorkItem> uploadQueue,
        CancellationToken ct)
    {
        var currentAttempt = item.TryCount + 1;
        var maxAttempts = maxRetries + 1;
        if (item.TryCount >= maxRetries)
        {
            Interlocked.Increment(ref _failedFiles);
            Log.Error(
                "Datei {FilePath} konnte nach {AttemptCount} Versuchen nicht hochgeladen werden.",
                item.FilePath,
                currentAttempt);
            return;
        }

        var nextAttempt = currentAttempt + 1;
        Log.Warning(
                "Upload fehlgeschlagen für {FilePath}, Versuch {CurrentAttempt}/{MaxAttempts}; Wiederholung {NextAttempt}/{MaxAttempts} wird eingeplant.",
            item.FilePath,
            currentAttempt,
            maxAttempts,
            nextAttempt);

        if (retryDelay > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(retryDelay), ct);
        }

        uploadQueue.Enqueue(item with { TryCount = currentAttempt });
    }

    private void CalculateTotalStatistics(
        ConcurrentQueue<UploadWorkItem> queue)
    {
        _totalFiles = queue.Count;
        _totalSize = 0;

        foreach (var item in queue)
        {
            try
            {
                _totalSize += item.ExpectedLength;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Zugriff auf {FilePath} für die Gesamtstatistik", item.FilePath);
            }
        }
    }

    private void ReportProgress()
    {
        var percentFiles = _totalFiles > 0 ? (float)_savedFiles / _totalFiles * 100 : 0;
        var percentSize = _totalSize > 0 ? (float)_savedSize / _totalSize * 100 : 0;
        var remainingFiles = _totalFiles - _savedFiles;
        var remainingSize = _totalSize - _savedSize;
        var elapsedTime = DateTime.Now - _startTime;
        var uploadSpeedMbPerSecond = elapsedTime.TotalSeconds > 0
            ? _savedSize / 1024.0 / 1024.0 / elapsedTime.TotalSeconds
            : 0;
        var remainingTime = TimeSpan.Zero;

        if (_savedSize > 0 && _totalSize > 0)
        {
            var completionFraction = (double)_savedSize / _totalSize;
            var estimatedTotalTime = TimeSpan.FromSeconds(elapsedTime.TotalSeconds / completionFraction);
            remainingTime = estimatedTotalTime - elapsedTime;
        }

        Log.Information(
            "Upload-Fortschritt: {SavedFiles}/{TotalFiles} Dateien ({PercentFiles:F1}%) - {SavedSizeMB:F2}/{TotalSizeMB:F2} MB ({PercentSize:F1}%) - Noch {RemainingFiles} Dateien ({RemainingMB:F2} MB) - {SpeedMBps:F2} MB/s - Verbleibend: {RemainingTime}",
            _savedFiles,
            _totalFiles,
            percentFiles,
            _savedSize / 1024.0 / 1024.0,
            _totalSize / 1024.0 / 1024.0,
            percentSize,
            remainingFiles,
            remainingSize / 1024.0 / 1024.0,
            uploadSpeedMbPerSecond,
            remainingTime);
    }
}
