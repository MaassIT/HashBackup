namespace HashBackup;

public class BackupJob(
    IStorageBackend backend,
    string sourceFolder,
    string metadataFile,
    int parallelUploads,
    bool safeMode,
    bool dryRun,
    int maxRetries,
    int retryDelay,
    string jobName,
    int targetDirDepth)
{
    public async Task RunAsync()
    {
        var hashes = safeMode ? await backend.FetchHashesAsync() : new Dictionary<string, long>();
        var uploadQueue = new ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)>();
        var filesIndiziert = 0;
        var filesToUpload = 0;
        var savedFiles = 0;
        var savedSize = 0L;
        var hashesToUpload = new HashSet<string>();
        var csvLines = new List<string>();
        var allFiles = Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories);

        foreach (var filePath in allFiles)
        {
            filesIndiziert++;
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
                fileHash = CalculateMd5(filePath);
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
                Log.Information("Upload erforderlich (mtime): {FilePath} (Backup-Mtime: {BackupMtime}, Datei-Mtime: {FileMtime})", filePath, backupMtime, fileInfo.LastWriteTimeUtc.ToFileTimeUtc());
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
        await File.WriteAllLinesAsync(metadataFile, csvLines);
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
                        var success = await backend.UploadToDestinationAsync(item.filePath, item.destPath, item.fileHash);
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
    }

    private static string CalculateMd5(string filePath)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }
}