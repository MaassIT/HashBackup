namespace HashBackup.Storage;

/// <summary>
/// Verfügbarkeitszustand eines gespeicherten Objekts. Archive-Objekte besitzen
/// weiterhin lesbare Eigenschaften, ihre Nutzdaten sind jedoch offline.
/// </summary>
public enum StorageObjectAvailability
{
    Missing,
    Online,
    Archived,
    Rehydrating
}

/// <summary>
/// Online-Tier, in das ein Archive-Blob rehydriert werden soll.
/// </summary>
public enum OnlineAccessTier
{
    Hot,
    Cool,
    Cold
}

/// <summary>
/// Azure-Priorität einer Archive-Rehydration.
/// </summary>
public enum ArchiveRehydratePriority
{
    Standard,
    High
}

/// <summary>
/// Speicherinformationen, die ohne Abruf des Blob-Inhalts geprüft werden können.
/// </summary>
public sealed record StorageObjectInfo(
    string ObjectPath,
    StorageObjectAvailability Availability,
    long? Length,
    string? ContentHash,
    string? AccessTier,
    string? ArchiveStatus)
{
    public static StorageObjectInfo Missing(string objectPath) =>
        new(objectPath, StorageObjectAvailability.Missing, null, null, null, null);
}

/// <summary>
/// Ergänzt ein Backup-Backend um die ausschließlich für Verify und Restore
/// benötigten Lese- und Rehydrationsoperationen.
/// </summary>
public interface IReadableStorageBackend
{
    Task<string?> FindLatestMetadataAsync(string jobName, CancellationToken ct = default);

    Task<StorageObjectInfo> InspectObjectAsync(string objectPath, CancellationToken ct = default);

    /// <summary>
    /// Erstellt mit möglichst wenigen Storage-Operationen einen Index der erwarteten
    /// Content-Hashes. Der tatsächliche Objektpfad kann eine andere Dateierweiterung
    /// besitzen als der Metadateneintrag, weil identische Inhalte dedupliziert werden.
    /// </summary>
    Task<IReadOnlyDictionary<string, StorageObjectInfo>> IndexContentObjectsAsync(
        IEnumerable<string> expectedHashes,
        CancellationToken ct = default);

    Task DownloadObjectAsync(string objectPath, string destinationPath, CancellationToken ct = default);

    Task RequestRehydrationAsync(
        IReadOnlyCollection<StorageObjectInfo> objects,
        OnlineAccessTier targetTier,
        ArchiveRehydratePriority priority,
        CancellationToken ct = default);
}
