namespace HashBackup.Storage;

public interface IStorageBackend
{
    /// <summary>
    /// Ruft die bereits vorhandenen Content-Hashes einschließlich der jeweiligen Dateigröße ab.
    /// </summary>
    Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lädt eine Datei zu ihrem Content-adressierten Ziel hoch.
    /// </summary>
    Task<UploadResult> UploadToDestinationAsync(
        string filePath,
        string destinationPath,
        string fileHash,
        bool isImportant = false,
        CancellationToken ct = default);

    /// <summary>
    /// Registriert Backend-spezifische sensible Daten für die Logmaskierung.
    /// </summary>
    void RegisterSensitiveData();
}
