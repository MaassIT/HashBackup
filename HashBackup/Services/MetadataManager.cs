namespace HashBackup.Services;

/// <summary>
/// Verwaltet die Metadaten des Backup-Prozesses.
/// </summary>
public sealed class MetadataManager(string metadataFile, IEnumerable<string> configDoku, string jobName, bool dryRun)
{
    private readonly List<string> _configDoku = configDoku.ToList();

    public async Task<List<string>> GenerateMetadataCsvAsync(
        Dictionary<string, (FileInfo Info, string Hash, bool UploadRequired)> filesInfo,
        CancellationToken ct = default)
    {
        var csvLines = new List<string>
        {
            $"Backup ausgeführt am: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };

        if (_configDoku.Any())
        {
            csvLines.AddRange(_configDoku);
        }

        csvLines.Add("EOF");
        csvLines.Add("");
        csvLines.Add("Filename,Hash,Extension,Size,Modified Time,InQueue");

        var currentDir = "";
        var filesByDirectory = filesInfo
            .GroupBy(f => Path.GetDirectoryName(f.Key) ?? string.Empty)
            .OrderBy(g => g.Key);

        foreach (var dirGroup in filesByDirectory)
        {
            var directory = dirGroup.Key;
            if (directory != currentDir)
            {
                csvLines.Add($"dir >> {directory}");
                currentDir = directory;
            }

            foreach (var fileEntry in dirGroup.OrderBy(f => f.Key))
            {
                var (fileInfo, fileHash, uploadRequired) = fileEntry.Value;
                csvLines.Add(string.Join(
                    ",",
                    EscapeCsvField(fileInfo.Name),
                    EscapeCsvField(fileHash),
                    EscapeCsvField(fileInfo.Extension),
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc.ToFileTimeUtc(),
                    uploadRequired ? "x" : ""));
            }
        }

        if (dryRun)
        {
            Log.Information("[DRY RUN] Würde Metadaten in Datei {MetadataFile} schreiben", metadataFile);
            return csvLines;
        }

        await File.WriteAllLinesAsync(metadataFile, csvLines, ct);
        Log.Information("Metadaten wurden in Datei {MetadataFile} geschrieben", metadataFile);
        return csvLines;
    }

    public async Task<bool> UploadBackupMetadataAsync(
        IStorageBackend backend,
        FileHashService hashService,
        CancellationToken ct = default)
    {
        try
        {
            if (dryRun)
            {
                Log.Information("[DRY RUN] Würde Metadaten-Datei {MetadataFile} in den Storage hochladen", metadataFile);
                return true;
            }

            var now = DateTime.Now;
            var metadataBlobName = $"metadata/{jobName}/{now:yyyy}/{now:MM}/backup_{now:yyyy-MM-dd_HH-mm-ss}.csv";
            var result = await backend.UploadToDestinationAsync(
                metadataFile,
                metadataBlobName,
                await hashService.CalculateMd5Async(metadataFile, ct),
                isImportant: true,
                ct);

            if (result.Success)
            {
                Log.Information("Backup-Metadaten hochgeladen nach {BlobName}", metadataBlobName);
                return true;
            }
            else
            {
                Log.Error("Fehler beim Hochladen der Backup-Metadaten nach {BlobName}", metadataBlobName);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Hochladen der Backup-Metadaten");
            return false;
        }
    }

    /// <summary>
    /// Escapes a value according to the default RFC 4180-compatible CSV dialect used by
    /// Python's csv.writer. This preserves the established metadata format while allowing
    /// commas, quotes and line breaks in file names.
    /// </summary>
    private static string EscapeCsvField(string value)
    {
        if (!value.Contains(',') &&
            !value.Contains('"') &&
            !value.Contains('\r') &&
            !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
