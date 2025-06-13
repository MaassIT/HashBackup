namespace HashBackup.Storage
{
    public enum BackupType
    {
        LocalStorage,
        Azure
    }

    public static class StorageBackendFactory
    {
        public static IStorageBackend Create(ConfigLoader config)
        {
            var backupTypeStr = config.Get("DEFAULT", "BACKUP_TYPE", "local_storage")?.ToLower();
            var backupType = ParseBackupType(backupTypeStr);
            Log.Debug("Erstelle Storage-Backend vom Typ: {BackupType}", backupType);
            
            switch (backupType)
            {
                case BackupType.Azure:
                    var account = config.Get("AZURE", "STORAGE_ACCOUNT") 
                                  ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_ACCOUNT");
                    var key = config.Get("AZURE", "STORAGE_KEY") 
                              ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_KEY");
                    var container = config.Get("AZURE", "CONTAINER") 
                                    ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:CONTAINER");
                    Log.Information("Azure Storage Backend wird initialisiert mit Account: {Account}, Container: {Container}", account, container);
                    return new AzureStorageBackend(account, key, container);
                
                case BackupType.LocalStorage:
                default:
                    var dest = config.Get("LOCAL_STORAGE", "DESTINATION") ?? throw new ArgumentException("Lokales Ziel fehlt in der Konfiguration! (LOCAL_STORAGE:DESTINATION)");
                    Log.Information("Lokales Storage Backend wird initialisiert mit Zielverzeichnis: {Destination}", dest);
                    return new LocalStorageBackend(dest);
            }
        }
        
        private static BackupType ParseBackupType(string? backupTypeStr)
        {
            return backupTypeStr switch
            {
                "azure" => BackupType.Azure,
                _ => BackupType.LocalStorage // Standardmäßig LocalStorage verwenden
            };
        }
    }
}
