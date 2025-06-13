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
            var backupType = config.Get("DEFAULT", "BACKUP_TYPE", "local_storage")?.ToLower();
            switch (backupType)
            {
                case "azure":
                    var account = config.Get("AZURE", "STORAGE_ACCOUNT") 
                                  ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_ACCOUNT");
                    var key = config.Get("AZURE", "STORAGE_KEY") 
                              ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:STORAGE_KEY");
                    var container = config.Get("AZURE", "CONTAINER") 
                                    ?? throw new ArgumentException("Fehlende Konfiguration: AZURE:CONTAINER");
                    return new AzureStorageBackend(account, key, container);
                default:
                    var dest = config.Get("LOCAL_STORAGE", "DESTINATION") ?? throw new ArgumentException("Lokales Ziel fehlt in der Konfiguration! (LOCAL_STORAGE:DESTINATION)");
                    return new LocalStorageBackend(dest);
            }
        }
    }
}
