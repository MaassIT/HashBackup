namespace HashBackup;

using Services;
using Utils;

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
    private readonly IgnorePatternMatcher _ignoreMatcher;
    
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
        
        // Einen einzigen Ignore-Pattern-Matcher für alle Muster initialisieren
        _ignoreMatcher = new IgnorePatternMatcher(config.IgnorePatterns);
        
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

        // Alle Dateien aus den Quellverzeichnissen sammeln und dabei Dateien und Verzeichnisse 
        // anhand der Ignorier-Muster filtern
        var allFiles = new List<string>();
        
        foreach (var sourceFolder in _config.SourceFolders)
        {
            try
            {
                var sourceDir = new DirectoryInfo(sourceFolder);
                if (!sourceDir.Exists)
                {
                    Log.Warning("Quellverzeichnis existiert nicht: {SourceFolder}", sourceFolder);
                    continue;
                }
                
                // Sammle alle Dateien rekursiv, filtere aber nach den Ignorier-Mustern
                var files = CollectFilesWithIgnorePatterns(sourceDir);
                allFiles.AddRange(files);
                
                Log.Information("Dateien aus {SourceFolder} hinzugefügt: {Count} Dateien", sourceFolder, files.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fehler beim Sammeln der Dateien aus {SourceFolder}", sourceFolder);
            }
        }
        
        Log.Information("Insgesamt {Count} Dateien zum Backup vorgemerkt", allFiles.Count);

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
            var lastWriteTimeUnixTimestamp = FileAttributesUtil.DateTimeToUnixTimestamp(fileInfo.LastWriteTimeUtc);
            
            if (_config.SafeMode)
            {
                if (!hashes.TryGetValue(fileHash, out var size) || size != fileInfo.Length)
                {
                    uploadRequired = true;
                }
                
                // Im Safe Mode: Wenn Hash-Upload nicht notwendig, aber die mtime sich geändert hat,
                // aktualisieren wir den Backup-Zeitstempel
                if (!uploadRequired)
                {
                    // Vergleiche den Backup-mtime mit der aktuellen Datei-mtime
                    if (string.IsNullOrEmpty(backupMtime) || backupMtime != lastWriteTimeUnixTimestamp)
                    {
                        Log.Debug("Korrigiere Sicherungsstatus für {FilePath}, da sich die Zeit geändert hat", filePath);
                        // Aktualisiere den Backup-mtime mit der aktuellen Datei-mtime
                        FileAttributesUtil.SetAttribute(filePath, backupMtimeAttr, lastWriteTimeUnixTimestamp);
                    }
                }
            }
            else
            {
                // Wenn kein Backup-mtime existiert oder die Zeiten nicht übereinstimmen
                if (string.IsNullOrEmpty(backupMtime) || backupMtime != lastWriteTimeUnixTimestamp)
                {
                    uploadRequired = true;
                }
            }

            // Für Metadaten-CSV speichern
            filesForMetadata[filePath] = (fileInfo, fileHash, uploadRequired);

            if (fileHash.StartsWith("SYM-"))
                uploadRequired = false;
            
            if (uploadRequired && fileInfo.Length > 0 && !hashesToUpload.Contains(fileHash))
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
            else if (fileHash.StartsWith("SYM-"))
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

        var tasks = new Task[2];
        
        // Führe die Uploads mit dem UploadCoordinator durch
        tasks[0] = _uploadCoordinator.UploadFilesAsync(uploadQueue, ct);
        
        // Hochladen der Metadaten-Datei in den Storage
        tasks[1] = _metadataManager.UploadBackupMetadataAsync(_backend, _fileHashService, ct);

        Task.WaitAll(tasks, ct);
    }
    
    /// <summary>
    /// Sammelt alle Dateien unter Berücksichtigung der Ignore-Muster
    /// </summary>
    /// <param name="directory">Das zu durchsuchende Verzeichnis</param>
    /// <returns>Liste der Dateipfade, die nicht ignoriert werden sollen</returns>
    private List<string> CollectFilesWithIgnorePatterns(DirectoryInfo directory)
    {
        var result = new List<string>();
        
        try 
        {
            // Überspringe ignorierte Verzeichnisse
            if (_ignoreMatcher.ShouldIgnore(directory.Name) || 
                _ignoreMatcher.ShouldIgnore(directory.FullName, true))
            {
                Log.Debug("Verzeichnis wird ignoriert: {Directory}", directory.FullName);
                return result;
            }
            
            // Sammle alle Dateien im aktuellen Verzeichnis
            foreach (var file in directory.EnumerateFiles())
            {
                // Überspringe ignorierte Dateien
                if (_ignoreMatcher.ShouldIgnore(file.Name) || 
                    _ignoreMatcher.ShouldIgnore(file.FullName, true))
                {
                    Log.Debug("Datei wird ignoriert: {File}", file.FullName);
                    continue;
                }
                
                result.Add(file.FullName);
            }
            
            // Rekursiv in Unterverzeichnissen fortsetzen
            foreach (var subDir in directory.EnumerateDirectories())
            {
                var subDirFiles = CollectFilesWithIgnorePatterns(subDir);
                result.AddRange(subDirFiles);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning("Keine Berechtigung für Verzeichnis {Directory}: {Message}", directory.FullName, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Durchsuchen von Verzeichnis {Directory}", directory.FullName);
        }
        
        return result;
    }
}