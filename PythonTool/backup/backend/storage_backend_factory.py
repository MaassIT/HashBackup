from config import BackupType, Settings

from .azure_backend import AzureStorageBackend
from .local_storage_backend import LocalStorageBackend

def create_storage_backend():
    if Settings.DEFAULT.BACKUP_TYPE == BackupType.AZURE:
        return AzureStorageBackend()
    
    elif Settings.DEFAULT.BACKUP_TYPE == BackupType.LOCAL_STORAGE:
        return LocalStorageBackend(Settings.LOCAL_STORAGE.DESTINATION)
    
    else:
        raise NotImplementedError("Kein passendes Storage-Backend gefunden.")