namespace HashBackup.Storage;

public enum BackupType
{
    LocalStorage,
    Azure
}

public static class StorageBackendFactory
{
    public static IStorageBackend Create(ConfigLoader config)
    {
        var backupTypeStr = config.Get("DEFAULT", "BACKUP_TYPE", "local_storage")?.ToLowerInvariant();
        var backupType = ParseBackupType(backupTypeStr);
        Log.Debug("Erstelle Storage-Backend vom Typ: {BackupType}", backupType);

        return backupType switch
        {
            BackupType.Azure => CreateAzureBackend(config),
            _ => CreateLocalBackend(config)
        };
    }

    private static IStorageBackend CreateAzureBackend(ConfigLoader config)
    {
        var account = config.Get("AZURE", "STORAGE_ACCOUNT")
                      ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_ACCOUNT");
        var key = config.Get("AZURE", "STORAGE_KEY")
                  ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_KEY");
        var container = config.Get("AZURE", "CONTAINER")
                        ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:CONTAINER");
        var storageTier = config.Get("AZURE", "STORAGE_TIER", "Cool");

        Log.Information(
            "Azure Storage Backend wird initialisiert mit Account: {Account}, Container: {Container}, Datentier: {StorageTier}",
            account,
            container,
            storageTier);
        return new AzureStorageBackend(account, key, container, AzureStorageBackend.ParseAccessTier(storageTier));
    }

    private static IStorageBackend CreateLocalBackend(ConfigLoader config)
    {
        var destination = config.Get("LOCAL_STORAGE", "DESTINATION")
                          ?? throw new ArgumentException("Lokales Ziel fehlt in der Konfiguration! (LOCAL_STORAGE:DESTINATION)");
        Log.Information("Lokales Storage Backend wird initialisiert mit Zielverzeichnis: {Destination}", destination);
        return new LocalStorageBackend(destination);
    }

    private static BackupType ParseBackupType(string? backupTypeStr) =>
        backupTypeStr switch
        {
            "azure" => BackupType.Azure,
            _ => BackupType.LocalStorage
        };
}
