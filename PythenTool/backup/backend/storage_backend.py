# storage_backend.py
from abc import ABC, abstractmethod

class StorageBackend(ABC):
    @abstractmethod
    def fetch_hashes(self) -> dict:
        """
        Lädt alle bekannten Hashes vom Ziel (Azure/Local/etc.) und gibt sie als Dict zurück.
        """
        pass

    @abstractmethod
    def upload_to_destination(self, file_path: str, destination_path: str, file_hash: str) -> bool:
        """
        Lädt die Datei file_path in das entsprechende Ziel hoch (z. B. Azure Blob oder lokales Verzeichnis).
        Gibt True zurück bei Erfolg, sonst False.
        """
        pass