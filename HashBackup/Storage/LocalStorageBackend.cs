namespace HashBackup.Storage;

public class LocalStorageBackend(string path) : IStorageBackend
{
    private const string AttrNameMd5HashValue = "user.md5_hash_value";

    public async Task<Dictionary<string, long>> FetchHashesAsync()
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

    public async Task<bool> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash)
    {
        var localDest = Path.Combine(path, destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDest)!);
        var alreadyExisted = File.Exists(localDest);
        if (!alreadyExisted)
        {
            try
            {
                File.Copy(filePath, localDest, overwrite: false);
                Log.Debug("Lokal gespeichert: {LocalDest}", localDest);
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