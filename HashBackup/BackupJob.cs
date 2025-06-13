namespace HashBackup;

public class BackupJob(
    IStorageBackend backend,
    IEnumerable<string> sourceFolders,
    string metadataFile,
    int parallelUploads,
    bool safeMode,
    bool dryRun,
    int maxRetries,
    int retryDelay,
    string jobName,
    int targetDirDepth,
    IEnumerable<string>? configDoku = null,
    IEnumerable<string>? ignoredFiles = null)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var hashes = safeMode ? await backend.FetchHashesAsync(ct) : new Dictionary<string, long>();
        var uploadQueue = new ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)>();
        var filesIndiziert = 0;
        var filesToUpload = 0;
        var savedFiles = 0;
        var savedSize = 0L;
        var hashesToUpload = new HashSet<string>();
        var csvLines = new List<string>
        {
            $"Backup ausgeführt am: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };

        // Entweder übergebene Konfigurationsdoku verwenden oder Standard-Konfiguration erstellen
        if (configDoku != null && configDoku.Any())
        {
            csvLines.AddRange(configDoku);
        }
        else
        {
            // Standard-Konfigurationsdokumentation erstellen
            csvLines.Add($"SOURCE_FOLDER={string.Join(";", sourceFolders)}");
            csvLines.Add($"JOB_NAME={jobName}");
            csvLines.Add($"SAFE_MODE={safeMode}");
            csvLines.Add($"DRY_RUN={dryRun}");
            csvLines.Add($"PARALLEL_UPLOADS={parallelUploads}");
            csvLines.Add($"MAX_RETRIES={maxRetries}");
            csvLines.Add($"RETRY_DELAY={retryDelay}");
            csvLines.Add($"TARGET_DIR_DEPTH={targetDirDepth}");
            csvLines.Add($"STORAGE_BACKEND={backend.GetType().Name}");
            
            if (ignoredFiles != null && ignoredFiles.Any())
            {
                csvLines.Add($"IGNORED_FILES={string.Join(", ", ignoredFiles)}");
            }
        }
        csvLines.Add("EOF");
        csvLines.Add("");
        
        // CSV-Header
        csvLines.Add("Filename,Hash,Extension,Size,Modified Time,InQueue");
        
        var ignoredFilesList = ignoredFiles?.ToList() ?? new List<string>();
        var allFiles = sourceFolders.SelectMany(sourceFolder => Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Where(f => !ignoredFilesList.Contains(Path.GetFileName(f)))
            .OrderBy(Path.GetDirectoryName))
            .ToList();

        var currentDir = "";

        foreach (var filePath in allFiles)
        {
            filesIndiziert++;
            
            // Verzeichniswechsel erkennen und in CSV vermerken
            var directory = Path.GetDirectoryName(filePath);
            if (directory != currentDir)
            {
                csvLines.Add($"dir >> {directory}");
                currentDir = directory;
            }
            
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;
            var fileExtension = fileInfo.Extension;
            var fileHash = FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_value");
            var fileHashMtime = FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_mtime");
            var backupMtimeAttr = $"user.{jobName}_backup_mtime";
            var backupMtime = FileAttributesUtil.GetAttribute(filePath, backupMtimeAttr);
            var uploadRequired = false;

            if (string.IsNullOrEmpty(fileHashMtime) || fileHashMtime != fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString() || string.IsNullOrEmpty(fileHash))
            {
                Log.Debug("Berechne Hash für Datei {FilePath} (letzte Änderung: {LastWriteTime})", filePath, fileInfo.LastWriteTimeUtc);
                fileHash = await CalculateMd5Async(filePath, ct);
                FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_value", fileHash);
                FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_mtime", fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString());
            }

            if (safeMode)
            {
                if (!hashes.ContainsKey(fileHash))
                {
                    uploadRequired = true;
                    Log.Information("Upload erforderlich: {FilePath} (Hash {FileHash} nicht im Ziel gefunden)", filePath, fileHash);
                }
                else
                {
                    // Hash ist im Ziel vorhanden, prüfe optional die Größe
                    if (hashes[fileHash] != fileSize)
                    {
                        Log.Warning("WARNUNG: Datei {FilePath} hat gleichen Hash, aber unterschiedliche Größe! Quelle: {SourceSize}, Ziel: {TargetSize}", filePath, fileSize, hashes[fileHash]);
                    }
                    else
                    {
                        Log.Information("Info: Datei {FilePath} ist bereits im Ziel vorhanden (Hash: {FileHash})", filePath, fileHash);
                    }
                }
                if (!uploadRequired && fileHashMtime != fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString())
                    FileAttributesUtil.SetAttribute(filePath, backupMtimeAttr, fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString());
            }
            else if (backupMtime != fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString())
            {
                uploadRequired = true;
                Log.Debug("Upload erforderlich (mtime): {FilePath} (Backup-Mtime: {BackupMtime}, Datei-Mtime: {FileMtime})", filePath, backupMtime, fileInfo.LastWriteTimeUtc.ToFileTimeUtc());
            }

            if (uploadRequired && fileSize > 0 && !hashesToUpload.Contains(fileHash))
            {
                // Zielpfad wie im Python-Tool: z.B. a/b/c/hash.ext für targetDirDepth=3
                var dirParts = new List<string>();
                for (var i = 0; i < Math.Min(targetDirDepth, fileHash.Length); i++)
                    dirParts.Add(fileHash[i].ToString());
                var destFilePath = $"{string.Join("/", dirParts)}/{fileHash}{fileExtension}";
                uploadQueue.Enqueue((filePath, destFilePath, fileHash, 0));
                hashesToUpload.Add(fileHash);
                filesToUpload++;
            }
            csvLines.Add($"{fileInfo.Name},{fileHash},{fileExtension},{fileSize},{fileInfo.LastWriteTimeUtc.ToFileTimeUtc()},{(uploadRequired && fileSize > 0 ? "x" : "")}");
        }

        // Schreibe Metadaten-CSV
        await File.WriteAllLinesAsync(metadataFile, csvLines, ct);
        Log.Information("Indizierung abgeschlossen, {FilesIndiziert} Dateien überprüft", filesIndiziert);
        Log.Information("Es müssen noch {FilesToUpload} Dateien hochgeladen werden", filesToUpload);

        // Parallele Uploads
        var tasks = new List<Task>();
        var cts = new CancellationTokenSource();
        for (var i = 0; i < parallelUploads; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (uploadQueue.TryDequeue(out var item))
                {
                    if (dryRun)
                    {
                        Log.Information("[DRY RUN] Würde Datei {FilePath} hochladen nach {DestPath}", item.filePath, item.destPath);
                        continue;
                    }
                    try
                    {
                        var success = await backend.UploadToDestinationAsync(item.filePath, item.destPath, item.fileHash, cts.Token);
                        if (success)
                        {
                            FileAttributesUtil.SetAttribute(item.filePath, $"user.{jobName}_backup_mtime", new FileInfo(item.filePath).LastWriteTimeUtc.ToFileTimeUtc().ToString());
                            Interlocked.Increment(ref savedFiles);
                            Interlocked.Add(ref savedSize, new FileInfo(item.filePath).Length);
                        }
                        else if (item.tryCount < maxRetries)
                        {
                            await Task.Delay(retryDelay * 1000, cts.Token);
                            uploadQueue.Enqueue((item.filePath, item.destPath, item.fileHash, item.tryCount + 1));
                        }
                        else
                        {
                            Log.Error("Datei {FilePath} konnte nicht hochgeladen werden. Versuche beendet", item.filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Fehler beim Upload von {FilePath}", item.filePath);
                    }
                }
            }, cts.Token));
        }
        await Task.WhenAll(tasks);
        Log.Information("Backup abgeschlossen: {SavedFiles} Dateien gesichert mit {SavedSizeMB:F2} MB", savedFiles, (float)savedSize / 1024 / 1024.0);
        
        // Hochladen der Metadaten-Datei in den Storage
        if (!dryRun)
        {
            await UploadBackupMetadataAsync();
        }
        else
        {
            Log.Information("[DRY RUN] Würde Metadaten-Datei {MetadataFile} in den Storage hochladen", metadataFile);
        }
    }

    private async Task UploadBackupMetadataAsync()
    {
        try
        {
            // Erstellen des Zeitstempels und des Ziel-Pfads wie im Python-Script
            var now = DateTime.Now;
            var year = now.ToString("yyyy");
            var month = now.ToString("MM");
            var timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
            
            var metadataBlobName = $"metadata/{jobName}/{year}/{month}/backup_{timestamp}.csv";
            
            var success = await backend.UploadToDestinationAsync(metadataFile, metadataBlobName, await CalculateMd5Async(metadataFile));
            
            if (success)
            {
                Log.Information("Backup-Metadaten hochgeladen nach {BlobName}", metadataBlobName);
            }
            else
            {
                Log.Error("Fehler beim Hochladen der Backup-Metadaten nach {BlobName}", metadataBlobName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Hochladen der Backup-Metadaten");
        }
    }

    private static async Task<string> CalculateMd5Async(string filePath, CancellationToken ct = default)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await md5.ComputeHashAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}