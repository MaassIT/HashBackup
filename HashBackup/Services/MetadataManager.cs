namespace HashBackup.Services;

/// <summary>
/// Verwaltet die Metadaten des Backup-Prozesses
/// </summary>
public class MetadataManager(string metadataFile, IEnumerable<string> configDoku, string jobName, bool dryRun)
{
    private readonly List<string> _configDoku = configDoku.ToList();

    /// <summary>
    /// Generiert eine CSV-Datei mit den Metadaten des Backups
    /// </summary>
    public async Task<List<string>> GenerateMetadataCsvAsync(
        Dictionary<string, (FileInfo Info, string Hash, bool UploadRequired)> filesInfo,
        CancellationToken ct = default)
    {
        var csvLines = new List<string>
        {
            $"Backup ausgeführt am: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };

        // Entweder übergebene Konfigurationsdoku verwenden oder Standard-Konfiguration erstellen
        if (_configDoku.Any())
        {
            csvLines.AddRange(_configDoku);
        }
        
        csvLines.Add("EOF");
        csvLines.Add("");
        
        // CSV-Header
        csvLines.Add("Filename,Hash,Extension,Size,Modified Time,InQueue");
        
        var currentDir = "";
        
        // Sortiere Dateien nach Verzeichnissen für übersichtlichere CSV
        var filesByDirectory = filesInfo
            .GroupBy(f => Path.GetDirectoryName(f.Key) ?? string.Empty)
            .OrderBy(g => g.Key);
            
        foreach (var dirGroup in filesByDirectory)
        {
            var directory = dirGroup.Key;
            
            // Verzeichniswechsel erkennen und in CSV vermerken
            if (directory != currentDir)
            {
                csvLines.Add($"dir >> {directory}");
                currentDir = directory;
            }
            
            // Dateien im aktuellen Verzeichnis
            foreach (var fileEntry in dirGroup.OrderBy(f => f.Key))
            {
                var (fileInfo, fileHash, uploadRequired) = fileEntry.Value;
                
                csvLines.Add($"{fileInfo.Name},{fileHash},{fileInfo.Extension},{fileInfo.Length},{fileInfo.LastWriteTimeUtc.ToFileTimeUtc()},{(uploadRequired && fileInfo.Length > 0 ? "x" : "")}");
            }
        }
        
        // Metadaten speichern
        await File.WriteAllLinesAsync(metadataFile, csvLines, ct);
        Log.Information("Metadaten wurden in Datei {MetadataFile} geschrieben", metadataFile);
        
        return csvLines;
    }

    /// <summary>
    /// Lädt eine Datei von der lokalen Metadaten-Datei ins Backend hoch
    /// </summary>
    public async Task UploadBackupMetadataAsync(
        IStorageBackend backend, 
        FileHashService hashService,
        CancellationToken ct = default)
    {
        try
        {
            if (dryRun)
            {
                Log.Information("[DRY RUN] Würde Metadaten-Datei {MetadataFile} in den Storage hochladen", metadataFile);
                return;
            }
            
            // Erstellen des Zeitstempels und des Ziel-Pfads wie im Python-Script
            var now = DateTime.Now;
            var year = now.ToString("yyyy");
            var month = now.ToString("MM");
            var timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
            
            var metadataBlobName = $"metadata/{jobName}/{year}/{month}/backup_{timestamp}.csv";
            
            // Metadaten als wichtig markieren, damit sie nicht im Archive-Tier gespeichert werden
            var success = await backend.UploadToDestinationAsync(metadataFile, metadataBlobName, 
                await hashService.CalculateMd5Async(metadataFile, ct), 
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
}
