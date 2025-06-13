import time
import os
import traceback
from azure.storage.blob import StandardBlobTier, BlobServiceClient # type: ignore
from azure.core.exceptions import ResourceExistsError

from .storage_backend import StorageBackend
from config import Settings

class AzureStorageBackend(StorageBackend):
    def __init__(self):
        # container_client stammt z. B. aus:
        # blob_service_client = BlobServiceClient(account_url=..., credential=...)
        # self.container_client = blob_service_client.get_container_client(...)
        blob_service_client = BlobServiceClient(account_url=f"https://{Settings.AZURE.STORAGE_ACCOUNT}.blob.core.windows.net", credential=Settings.AZURE.STORAGE_KEY)
        container_client = blob_service_client.get_container_client(Settings.AZURE.CONTAINER)
        self.container_client = container_client
        self.ATTR_NAME_BACKUP_MTIME = f"user.{Settings.DEFAULT.JOB_NAME}_backup_mtime"

    def fetch_hashes(self) -> dict:
        hashes = {}
        blobs = self.container_client.list_blobs()
        errors = []
        for blob in blobs:
            try:
                if blob.content_settings and blob.content_settings.content_md5 and blob.size > 0:
                    hashes[blob.content_settings.content_md5.hex()] = blob.size
                elif blob.size > 0:
                    # Fallback (ggf. anpassen)
                    hashes[os.path.splitext(os.path.basename(blob.name))[0]] = blob.size
            except Exception as e:
                errors.append(f"{e}: {blob}\n\n")
        if errors:
            raise ValueError("\n\n".join(errors))
        return hashes

    def upload_to_destination(self, file_path: str, destination_path: str, file_hash: str) -> bool:
        if Settings.DEFAULT.DRY_RUN:
            print(f"[DRY RUN] Würde Datei {file_path} hochladen nach {destination_path}.")
            return True
        
        blob_client = self.container_client.get_blob_client(destination_path)

        # Abbruch-Flag könntest du ggf. von außen übergeben
        try:
            file_size = os.path.getsize(file_path)
            if file_size > 65 * 1024 * 1024:
                print(f"Lade Datei mit {file_size / 1024 / 1024} MB hoch: {destination_path}")
            with open(file_path, "rb") as data:
                blob_client.upload_blob(
                    data,
                    overwrite=False,
                    standard_blob_tier=get_standard_blob_tier(Settings.AZURE.STORAGE_TIER),
                    connection_timeout=14400
                )
            print(f"Hochgeladen: {destination_path}")
            return True
        except ResourceExistsError as e:
            print(f"{destination_path} existiert bereits.")
            return True
        except Exception as e:
            print(f"Fehler beim Hochladen von {file_path} nach {destination_path}: {e}")
            traceback.print_exc()  # Gibt den gesamten Traceback aus
            return False

def get_standard_blob_tier(string_tier: str) -> StandardBlobTier:
    storage_tier_upper = string_tier.strip().upper()
    valid_tiers = {tier.name: tier for tier in StandardBlobTier}
    if storage_tier_upper in valid_tiers:
        return valid_tiers[storage_tier_upper]
    else:
        raise ValueError(f"Ungültiger Storage-Tier: {string_tier}")