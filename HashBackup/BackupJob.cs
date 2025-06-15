namespace HashBackup;

using Services;

/// <summary>
/// Hauptklasse für die Ausführung eines Backup-Jobs
/// </summary>
public class BackupJob
{
    private readonly IStorageBackend _backend;
    private readonly BackupConfiguration _config;
    private readonly FileHashService _fileHashService;
    private readonly MetadataManager _metadataManager;
    private readonly UploadCoordinator _uploadCoordinator;
    
    // Standardwert für parallele Hash-Berechnungen
    private readonly int _parallelHashCalculations;
    
    public BackupJob(
        IStorageBackend backend,
        BackupConfiguration config,
        IEnumerable<string>? configDoku = null)
    {
        _backend = backend;
        _config = config;
        var configDoku1 = configDoku ?? [];

        // Services initialisieren
        _fileHashService = new FileHashService();
        _metadataManager = new MetadataManager(
            config.MetadataFile, 
            configDoku1, 
            config.JobName, 
            config.DryRun);
        _uploadCoordinator = new UploadCoordinator(
            backend,
            config.ParallelUploads,
            config.MaxRetries,
            config.RetryDelay,
            config.DryRun,
            config.JobName);
        
        // Parallele Hash-Berechnungen auf Basis der CPU-Kerne festlegen
        _parallelHashCalculations = Math.Max(1, Environment.ProcessorCount - 1);
    }

    /// <summary>
    /// Führt den Backup-Job aus
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // Starte das Abrufen der Hashes vom Backend asynchron, damit es parallel zur weiteren Verarbeitung läuft
        var hashesTask = _config.SafeMode ? _backend.FetchHashesAsync(ct) : Task.FromResult(new Dictionary<string, long>());
        
        var uploadQueue = new ConcurrentQueue<(string filePath, string destPath, string fileHash, int tryCount)>();
        var filesIndiziert = 0;
        var filesToUpload = 0;
        var hashesToUpload = new HashSet<string>();

        // Liste der zu ignorierenden Dateien
        var ignoredFilesList = _config.IgnoredFiles;
        
        // Alle Dateien aus den Quellverzeichnissen sammeln
        var allFiles = _config.SourceFolders.SelectMany(sourceFolder => Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Where(f => !ignoredFilesList.Contains(Path.GetFileName(f)))
            .OrderBy(Path.GetDirectoryName))
            .ToList();

        // Liste der Attribute, die wir für jede Datei benötigen
        var attributesToLoad = new[] {
            "user.md5_hash_value",
            "user.md5_hash_mtime",
            $"user.{_config.JobName}_backup_mtime"
        };
        
        // Vorladen aller Attribute für alle Dateien - deutlich schneller als einzelne Abfragen
        Log.Information("Lade Dateiattribute für {Count} Dateien vor...", allFiles.Count);
        var startTime = DateTime.Now;
        FileAttributesUtil.PreloadAttributes(allFiles, attributesToLoad);
        
        Log.Information("Dateiattribute für {Count} Dateien in {ElapsedMs}ms geladen", 
            allFiles.Count, (DateTime.Now - startTime).TotalMilliseconds);

        // Vorbereitung für parallele Hash-Berechnung
        var fileInfos = new ConcurrentDictionary<string, (FileInfo Info, string? Hash, string? HashMtime, string? BackupMtime, string BackupMtimeAttr)>();
        
        Log.Information("Rufe Dateiattribute und Hashes ab für {Count} Dateien", allFiles.Count);
        
        // Vorbereiten der Dateien zur parallelen Verarbeitung - jetzt deutlich schneller dank Cache
        foreach (var filePath in allFiles)
        {
            var fileInfo = new FileInfo(filePath);
            var backupMtimeAttr = $"user.{_config.JobName}_backup_mtime";
            
            // Speichere grundlegende Dateiinformationen
            fileInfos[filePath] = (
                fileInfo, 
                FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_value"),
                FileAttributesUtil.GetAttribute(filePath, "user.md5_hash_mtime"),
                FileAttributesUtil.GetAttribute(filePath, backupMtimeAttr),
                backupMtimeAttr
            );
        }

        // Starten der parallelen Hash-Berechnung mit dem FileHashService
        Log.Information("Starte parallele Hash-Berechnung für {Count} Dateien mit {ThreadCount} Threads", 
            allFiles.Count, _parallelHashCalculations);
            
        await _fileHashService.CalculateHashesInParallelAsync(fileInfos, _parallelHashCalculations, ct);
        
        // Warte auf das Abrufen der Hashes vom Backend, falls es noch nicht abgeschlossen ist
        var hashes = await hashesTask;
        Log.Information("Hash-Abruf vom Backend abgeschlossen: {Count} Hashes gefunden", hashes.Count);

        // Erstelle ein Dictionary zur Erfassung der Dateien für die Metadaten-CSV
        var filesForMetadata = new Dictionary<string, (FileInfo Info, string Hash, bool UploadRequired)>();

        // Verarbeite alle Dateien und entscheide, welche hochgeladen werden müssen
        foreach (var filePath in allFiles)
        {
            filesIndiziert++;
            
            var (fileInfo, fileHash, fileHashMtime, backupMtime, backupMtimeAttr) = fileInfos[filePath];
            
            // Sicherstellen, dass ein Hash vorhanden ist
            if (string.IsNullOrEmpty(fileHash))
            {
                Log.Warning("Kein Hash verfügbar für Datei {FilePath}, überspringe", filePath);
                continue;
            }
            
            var uploadRequired = false;

            if (_config.SafeMode)
            {
                if (!hashes.ContainsKey(fileHash))
                {
                    uploadRequired = true;
                    Log.Information("Upload erforderlich: {FilePath} (Hash {FileHash} nicht im Ziel gefunden)", filePath, fileHash);
                }
                else
                {
                    // Hash ist im Ziel vorhanden, prüfe optional die Größe
                    if (hashes[fileHash] != fileInfo.Length)
                    {
                        Log.Warning("WARNUNG: Datei {FilePath} hat gleichen Hash, aber unterschiedliche Größe! Quelle: {SourceSize}, Ziel: {TargetSize}", filePath, fileInfo.Length, hashes[fileHash]);
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

            // Für Metadaten-CSV speichern
            filesForMetadata[filePath] = (fileInfo, fileHash, uploadRequired);

            if (uploadRequired && fileInfo.Length > 0 && !hashesToUpload.Contains(fileHash) && !fileHash.StartsWith("SYM-"))
            {
                // Zielpfad wie im Python-Tool: z.B. a/b/c/hash.ext für targetDirDepth=3
                var dirParts = new List<string>();
                for (var i = 0; i < Math.Min(_config.TargetDirDepth, fileHash.Length); i++)
                    dirParts.Add(fileHash[i].ToString());
                var destFilePath = $"{string.Join("/", dirParts)}/{fileHash}{fileInfo.Extension}";
                uploadQueue.Enqueue((filePath, destFilePath, fileHash, 0));
                hashesToUpload.Add(fileHash);
                filesToUpload++;
                Log.Debug("Datei für Upload eingeplant: {FilePath}", filePath);
            }
            else if (fileHash.StartsWith("SYM-") && uploadRequired)
            {
                Log.Debug("Symbolischer Link erkannt und nur in Metadaten erfasst (kein Upload): {FilePath}", filePath);
                // Wir markieren den Link als gesichert, indem wir den Backup-Mtime aktualisieren
                FileAttributesUtil.SetAttribute(filePath, backupMtimeAttr, fileInfo.LastWriteTimeUtc.ToFileTimeUtc().ToString());
            }
        }

        // Schreibe Metadaten-CSV mit dem MetadataManager
        await _metadataManager.GenerateMetadataCsvAsync(filesForMetadata, ct);
        Log.Information("Indizierung abgeschlossen, {FilesIndiziert} Dateien überprüft", filesIndiziert);
        Log.Information("Es müssen noch {FilesToUpload} Dateien hochgeladen werden", filesToUpload);

        // Cache leeren, um Speicher freizugeben
        FileAttributesUtil.ClearCache();

        // Führe die Uploads mit dem UploadCoordinator durch
        await _uploadCoordinator.UploadFilesAsync(uploadQueue, ct);
        
        // Hochladen der Metadaten-Datei in den Storage
        await _metadataManager.UploadBackupMetadataAsync(_backend, _fileHashService, ct);
    }
}