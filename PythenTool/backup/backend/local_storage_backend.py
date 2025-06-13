import os
import shutil
import xattr

from .storage_backend import StorageBackend
from config import Settings

ATTR_NAME_MD5_HASH_VALUE = "user.md5_hash_value"

class LocalStorageBackend(StorageBackend):
    def __init__(self, destination_path):
        # z.B. Settings.LOCAL_STORAGE.DESTINATION
        self.destination_path = destination_path

    def fetch_hashes(self) -> dict:
        hashes = {}
        for root, _, files in os.walk(self.destination_path):
            for filename in files:
                file_path = os.path.join(root, filename)
                # Hier nur Beispiel - wie in deinem Code
                if b"user.backup_hash" in xattr.listxattr(file_path):
                    hashes[xattr.getxattr(file_path, "user.backup_hash").decode()] = os.path.getsize(file_path)
        return hashes

    def upload_to_destination(self, file_path: str, destination_path: str, file_hash: str) -> bool:
        if Settings.DEFAULT.DRY_RUN:
            print(f"[DRY RUN] Würde Datei {file_path} lokal speichern unter {destination_path}.")
            return True
               
        local_dest = os.path.join(self.destination_path, destination_path)
        os.makedirs(os.path.dirname(local_dest), exist_ok=True)
        shutil.copy2(file_path, local_dest)
        
        xattr.setxattr(local_dest, ATTR_NAME_MD5_HASH_VALUE, file_hash.encode)
        print(f"Lokal gespeichert: {local_dest}")
        return True