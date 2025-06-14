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
    // Standardwert für parallele Hash-Berechnungen
    private readonly int _parallelHashCalculations = Math.Max(1, Environment.ProcessorCount - 1);

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Starte das Abrufen der Hashes vom Backend asynchron, damit es parallel zur weiteren Verarbeitung läuft
        var hashesTask = safeMode ? backend.FetchHashesAsync(ct) : Task.FromResult(new Dictionary<string, long>());
        
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
            csvLines.Add($"PARALLEL_HASH_CALCULATIONS={_parallelHashCalculations}");
            
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

        // Vorbereitung für parallele Hash-Berechnung
        var fileInfos = new ConcurrentDictionary<string, (FileInfo Info, string? Hash, string? HashMtime, string? BackupMtime, string BackupMtimeAttr)>();
        
        Log.Information("Rufe Dateiattribute und Hashes ab für {Count} Dateien", allFiles.Count);
        
        // Vorbereiten der Dateien zur parallelen Verarbeitung
        foreach (var filePath in allFiles)
        {
            var fileInfo = new FileInfo(filePath);
            var backupMtimeAttr = $"user.{jobName}_backup_mtime";
            
            // Speichere grundlegende Dateiinformationen
            fileInfos[filePath] = (fileInfo, 
                FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_value"),
                FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_mtime"),
                FileAttributesUtil.GetAttribute(filePath, backupMtimeAttr),
                backupMtimeAttr);
        }

        // Starten der parallelen Hash-Berechnung für Dateien, die einen neuen Hash benötigen
        Log.Information("Starte parallele Hash-Berechnung für {Count} Dateien mit {ThreadCount} Threads", allFiles.Count, _parallelHashCalculations);
        var filesToHash = allFiles.Where(filePath => 
        {
            var (info, hash, hashMtime, _, _) = fileInfos[filePath];
            return string.IsNullOrEmpty(hashMtime) || 
                   hashMtime != info.LastWriteTimeUtc.ToFileTimeUtc().ToString() || 
                   string.IsNullOrEmpty(hash);
        }).ToList();

        var hashTasks = new List<Task>();
        var hashSemaphore = new SemaphoreSlim(_parallelHashCalculations);
        var hashesComputed = 0;
        
        foreach (var filePath in filesToHash)
        {
            await hashSemaphore.WaitAsync(ct);
            
            hashTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var (info, _, _, _, _) = fileInfos[filePath];
                    Log.Debug("Berechne Hash für Datei {FilePath} (letzte Änderung: {LastWriteTime})", filePath, info.LastWriteTimeUtc);
                    
                    var fileHash = await CalculateMd5Async(filePath, ct);
                    FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_value", fileHash);
                    FileAttributesUtil.SetAttribute(filePath, "user.md5_hash_mtime", info.LastWriteTimeUtc.ToFileTimeUtc().ToString());
                    
                    // Aktualisiere den Hash in unserer Dictionary
                    var current = fileInfos[filePath];
                    fileInfos[filePath] = (current.Info, fileHash, info.LastWriteTimeUtc.ToFileTimeUtc().ToString(), current.BackupMtime, current.BackupMtimeAttr);
                    
                    Interlocked.Increment(ref hashesComputed);
                    if (hashesComputed % 100 == 0)
                    {
                        Log.Information("{Count}/{Total} Hashes berechnet ({Percent:F1}%)", 
                            hashesComputed, filesToHash.Count, (float)hashesComputed / filesToHash.Count * 100);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Fehler bei der Hash-Berechnung für {FilePath}", filePath);
                }
                finally
                {
                    hashSemaphore.Release();
                }
            }, ct));
        }

        // Warten auf Abschluss der Hash-Berechnung
        if (hashTasks.Any())
        {
            Log.Information("Warte auf Abschluss der parallelen Hash-Berechnung...");
            await Task.WhenAll(hashTasks);
            Log.Information("Hash-Berechnung abgeschlossen für {Count} Dateien", hashesComputed);
        }
        else
        {
            Log.Information("Keine neuen Hashes zu berechnen");
        }
        
        // Warte auf das Abrufen der Hashes vom Backend, falls es noch nicht abgeschlossen ist
        var hashes = await hashesTask;
        Log.Information("Hash-Abruf vom Backend abgeschlossen: {Count} Hashes gefunden", hashes.Count);

        // Verarbeite alle Dateien und entscheide, welche hochgeladen werden müssen
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
            
            var (fileInfo, fileHash, fileHashMtime, backupMtime, backupMtimeAttr) = fileInfos[filePath];
            var fileSize = fileInfo.Length;
            var fileExtension = fileInfo.Extension;
            var uploadRequired = false;

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
                        var success = await backend.UploadToDestinationAsync(item.filePath, item.destPath, item.fileHash, false, cts.Token);
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
            await UploadBackupMetadataAsync(cts.Token);
        }
        else
        {
            Log.Information("[DRY RUN] Würde Metadaten-Datei {MetadataFile} in den Storage hochladen", metadataFile);
        }
    }

    private async Task UploadBackupMetadataAsync(CancellationToken ct = default)
    {
        try
        {
            // Erstellen des Zeitstempels und des Ziel-Pfads wie im Python-Script
            var now = DateTime.Now;
            var year = now.ToString("yyyy");
            var month = now.ToString("MM");
            var timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
            
            var metadataBlobName = $"metadata/{jobName}/{year}/{month}/backup_{timestamp}.csv";
            
            // Metadaten als wichtig markieren, damit sie nicht im Archive-Tier gespeichert werden
            var success = await backend.UploadToDestinationAsync(metadataFile, metadataBlobName, 
                await CalculateMd5Async(metadataFile, ct), 
                isImportant: true, ct: ct);
            
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