namespace HashBackup.Storage
{
    public interface IStorageBackend
    {
        /// <summary>
        /// Lädt alle bekannten Hashes vom Ziel (Azure/Local/etc.) und gibt sie als Dictionary zurück.
        /// </summary>
        Task<Dictionary<string, long>> FetchHashesAsync(CancellationToken ct = default);

        /// <summary>
        /// Lädt die Datei filePath in das entsprechende Ziel hoch (z. B. Azure Blob oder lokales Verzeichnis).
        /// Gibt true zurück bei Erfolg, sonst false.
        /// </summary>
        /// <param name="filePath">Der lokale Pfad der hochzuladenden Datei</param>
        /// <param name="destinationPath">Der Pfad im Ziel-Storage</param>
        /// <param name="fileHash">Der Hash der Datei</param>
        /// <param name="isImportant">Gibt an, ob die Datei wichtig ist und nicht im Archive-Tier gespeichert werden soll</param>
        /// <param name="ct">Cancellation Token</param>
        Task<bool> UploadToDestinationAsync(string filePath, string destinationPath, string fileHash, bool isImportant = false, CancellationToken ct = default);
    }
}
