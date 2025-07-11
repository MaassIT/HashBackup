namespace HashBackup.Storage;

public class LocalStorageBackend(string path) : IStorageBackend
{
    private const string AttrNameMd5HashValue = "user.md5_hash_value";

    /// <summary>
    /// Registriert sensible Daten dieses Backends, die in Logs und Ausgaben maskiert werden sollen
    /// </summary>
    public void RegisterSensitiveData()
    {
        // Bei LocalStorageBackend gibt es keine sensitiven Daten zum Registrieren
        Log.Debug("Lokales Storage Backend: Keine sensiblen Daten zu registrieren");
    }

    public async Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default)
    {
        var hashes = new Dictionary<string, long>();
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            // Hash aus Dateinamen extrahieren (ohne Extension)
            var fileName = Path.GetFileNameWithoutExtension(file);
            var size = new FileInfo(file).Length;
            if (!string.IsNullOrEmpty(fileName) && size > 0)
                hashes[fileName] = size;
        }
        return await Task.FromResult(hashes);
    }

    public async Task<bool> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash, bool isImportant = false, CancellationToken ct = default)
    {
        var localDest = Path.Combine(path, destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDest)!);
        var alreadyExisted = File.Exists(localDest);
        if (!alreadyExisted)
        {
            try
            {
                File.Copy(filePath, localDest, overwrite: false);
                
                // Für lokale Speicherung hat isImportant keine Auswirkung, aber wir können es für Debugging-Zwecke loggen
                if (isImportant)
                {
                    Log.Debug("Wichtige Datei lokal gespeichert: {LocalDest}", localDest);
                }
                else
                {
                    Log.Debug("Lokal gespeichert: {LocalDest}", localDest);
                }
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x50) // ERROR_FILE_EXISTS
            {
                Log.Information("{LocalDest} existiert bereits (catch). Skipping", localDest);
            }
            catch (Exception ex)
            {
                Log.Error("Fehler beim lokalen Kopieren von {FilePath} nach {LocalDest}: {ExMessage}", filePath, localDest, ex.Message);
                return await Task.FromResult(false);
            }
        }
        // Attribut im Ziel ist nicht nötig, aber im Quellfile für Backup-Status
        FileAttributesUtil.SetAttribute(filePath, AttrNameMd5HashValue, fileHash);
        FileAttributesUtil.SetAttribute(filePath, $"user.backup_mtime", new FileInfo(filePath).LastWriteTimeUtc.ToFileTimeUtc().ToString());
        return await Task.FromResult(true);
    }
}