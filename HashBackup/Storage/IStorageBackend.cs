namespace HashBackup.Storage
{
    public interface IStorageBackend
    {
        /// <summary>
        /// Lädt alle bekannten Hashes vom Ziel (Azure/Local/etc.) und gibt sie als Dictionary zurück.
        /// </summary>
        Task<Dictionary<string, long>> FetchHashesAsync();

        /// <summary>
        /// Lädt die Datei filePath in das entsprechende Ziel hoch (z. B. Azure Blob oder lokales Verzeichnis).
        /// Gibt true zurück bei Erfolg, sonst false.
        /// </summary>
        Task<bool> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash);
    }
}
