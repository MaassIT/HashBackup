import configparser
import argparse
import os
from typing import Optional
from enum import Enum

DEFAULT_SECTION_NAME = "DEFAULT"
AZURE_SECTION_NAME = "AZURE"
LOCAL_STORAGE_SECTION_NAME = "LOCAL_STORAGE"

class BackupType(Enum):
    LOCAL_STORAGE = LOCAL_STORAGE_SECTION_NAME.lower()
    AZURE = AZURE_SECTION_NAME.lower()

class ConfigValidationError(Exception):
    """Fehlerklasse für fehlende oder ungültige Konfigurationswerte."""
    pass

def ArgParserAddArguments(parser: argparse.ArgumentParser):
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.BACKUP_TYPE", type=str)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.SOURCE_FOLDER", type=str)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.BACKUP_METADATA_FILE", type=str)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.MAX_RETRIES", type=int)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.RETRY_DELAY", type=int)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.VERBOSE", "--VERBOSE", "-v", dest=f"{DEFAULT_SECTION_NAME}.VERBOSE", action="store_const", const=True, default=argparse.SUPPRESS)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.SAFE_MODE", "--safe", action="store_const", const=True, default=argparse.SUPPRESS)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.DRY_RUN", action="store_const", const=True, default=argparse.SUPPRESS)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.PARALLEL_UPLOADS", type=int)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.INTERACTIVE_MODE", "--INTERACTIVE", "-i", dest=f"{DEFAULT_SECTION_NAME}.INTERACTIVE_MODE", action="store_const", const=True, default=argparse.SUPPRESS)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.JOB_NAME", type=str)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.LOCK_FILE", type=str)
    parser.add_argument(f"--{DEFAULT_SECTION_NAME}.TARGET_DIR_DEPTH", type=int)
    parser.add_argument(f"--{AZURE_SECTION_NAME}.STORAGE_ACCOUNT", type=str)
    parser.add_argument(f"--{AZURE_SECTION_NAME}.STORAGE_KEY", type=str)
    parser.add_argument(f"--{AZURE_SECTION_NAME}.CONTAINER", type=str)
    parser.add_argument(f"--{AZURE_SECTION_NAME}.STORAGE_TIER", type=str)
    parser.add_argument(f"--{AZURE_SECTION_NAME}.RETENTION_DAYS", type=int)
    parser.add_argument(f"--{LOCAL_STORAGE_SECTION_NAME}.DESTINATION", type=str)

class ConfigHelper:
       
    def __init__(self, args:argparse.Namespace, config):
        self.ARGS = args
        self.CONFIG:configparser.ConfigParser = config
        self.CONFIG_DOKU = []

    def get_value(self, section:str, key:str, default=None, requied=False, secret=False):
        secret_text = "ausgeblendet"

        # 1. CLI-Argumente prüfen
        cli_key = f"{section}.{key}".upper()
        if self.ARGS and hasattr(self.ARGS, cli_key):
            value = getattr(self.ARGS, cli_key, None)
            if value is not None:
                self.CONFIG_DOKU.append(f"CLI         > {section}.{key} = {value if not secret else secret_text}")
                return value
        
        # 2. Umgebungsvariablen prüfen
        if cli_key in os.environ:
            self.CONFIG_DOKU.append(f"Environment > {section}.{key} = {os.environ[cli_key] if not secret else secret_text}")
            return os.environ[cli_key]
        
        # 3. Konfigurationsdatei prüfen
        if section in self.CONFIG and key in self.CONFIG[section]:
            self.CONFIG_DOKU.append(f"Config      > {section}.{key} = {self.CONFIG[section][key] if not secret else secret_text}")
            return self.CONFIG[section].get(key)
        
        if default is not None:
            self.CONFIG_DOKU.append(f"Default     > {section}.{key} = {default if not secret else secret_text}")
            return default
        
        if requied:
            raise ConfigValidationError(f"Fehlendes erforderliches Feld in [{section}]: {key}")
    
    def get_bool(self, section:str, key:str, default:bool=None, requied=False):
        """Holt eine boolesche Konfigurationsoption."""
        value = self.get_value(section, key, default, requied)
        return str(value).strip().lower() in ("1", "true", "yes", "on")
    
    def get_int(self, section:str, key:str, default:int=None, requied=False):
        """Holt einen int Konfigurationsoption."""
        return int(self.get_value(section, key, default, requied))

class DefaultSettings:
    """Speichert die allgemeinen Backup-Konfigurationen."""
    def __init__(self, config: ConfigHelper):
        if not isinstance(config, ConfigHelper):
            raise TypeError("config muss eine Instanz von ConfigHelper sein")
        self.config = config  # Hier die Instanz speichern!

        try:
            backup_type_str = config.get_value(DEFAULT_SECTION_NAME, "BACKUP_TYPE", requied=True).lower()
            self.BACKUP_TYPE = BackupType(backup_type_str)
        except ValueError:
            raise ValueError(f"Ungültiger BACKUP_TYPE in der Konfigurationsdatei: {backup_type_str}")

        self.IGNORED_FILES = [".DS_Store", "Thumbs.db", "desktop.ini"]
        self.SOURCE_FOLDER = config.get_value(DEFAULT_SECTION_NAME, "SOURCE_FOLDER")
        self.BACKUP_METADATA_FILE = config.get_value(DEFAULT_SECTION_NAME, "BACKUP_METADATA_FILE")
        self.MAX_RETRIES = config.get_int(DEFAULT_SECTION_NAME, "MAX_RETRIES", default=3)
        self.RETRY_DELAY = config.get_int(DEFAULT_SECTION_NAME, "RETRY_DELAY", default=5)
        self.VERBOSE = config.get_bool(DEFAULT_SECTION_NAME, "VERBOSE", default=False)
        self.SAFE_MODE = config.get_bool(DEFAULT_SECTION_NAME, "SAFE_MODE", default=False)
        self.DRY_RUN = config.get_bool(DEFAULT_SECTION_NAME, "DRY_RUN", default=False)
        self.PARALLEL_UPLOADS = config.get_int(DEFAULT_SECTION_NAME, "PARALLEL_UPLOADS", default=1)
        self.INTERACTIVE_MODE = config.get_bool(DEFAULT_SECTION_NAME, "INTERACTIVE_MODE", default=False)
        self.JOB_NAME = config.get_value(DEFAULT_SECTION_NAME, "JOB_NAME", default="Default")
        self.LOCK_FILE = config.get_value(DEFAULT_SECTION_NAME, "LOCK_FILE", default="/opt/backup_lock")
        self.TARGET_DIR_DEPTH = config.get_int(DEFAULT_SECTION_NAME, "TARGET_DIR_DEPTH", default=3)

class AzureSettings:
    """Speichert die Azure-spezifischen Konfigurationen."""
    def __init__(self, config:ConfigHelper):
        self.STORAGE_ACCOUNT = config.get_value(AZURE_SECTION_NAME, "STORAGE_ACCOUNT", requied=True)
        self.STORAGE_KEY = config.get_value(AZURE_SECTION_NAME, "STORAGE_KEY", requied=True, secret=True)
        self.CONTAINER = config.get_value(AZURE_SECTION_NAME, "CONTAINER", requied=True)
        self.STORAGE_TIER = config.get_value(AZURE_SECTION_NAME, "STORAGE_TIER", default="Cool")
        self.RETENTION_DAYS = config.get_int(AZURE_SECTION_NAME, "RETENTION_DAYS", default=30)

class LocalStorageSettings:
    """Speichert die lokalen Speicheroptionen."""
    def __init__(self, config:ConfigHelper):
        self.DESTINATION = config.get_value(LOCAL_STORAGE_SECTION_NAME, "DESTINATION", requied=True)

class Settings:
    """Hauptklasse für die Konfigurationsverwaltung."""
    DEFAULT:DefaultSettings = None
    AZURE:AzureSettings = None
    LOCAL_STORAGE:LocalStorageSettings = None
    CONFIG_DOKU = []

    @staticmethod
    def load(args:argparse.Namespace):
        """Lädt die Konfiguration aus einer Datei und speichert sie in den Settings-Klassen."""
        config = configparser.ConfigParser()
        config.read(args.config)

        configHelper = ConfigHelper(args, config)

        print(f"Konfigurationsdatei: {args.config}")

        Settings.DEFAULT = DefaultSettings(configHelper)

        if Settings.DEFAULT is None:
            raise RuntimeError("Fehler: Settings.DEFAULT konnte nicht geladen werden.")

        # Nur Azure laden, wenn es in der Konfiguration existiert
        if Settings.DEFAULT.BACKUP_TYPE == BackupType.AZURE:
            Settings.AZURE = AzureSettings(configHelper)
        elif Settings.DEFAULT.BACKUP_TYPE == BackupType.LOCAL_STORAGE:
            Settings.LOCAL_STORAGE = LocalStorageSettings(configHelper)

        Settings.CONFIG_DOKU = configHelper.CONFIG_DOKU
        print("\n".join(configHelper.CONFIG_DOKU))

# **📌 Direkt eine globale Instanz erzeugen, die beim Import verfügbar ist**
Settings = Settings  # Globale Instanz für einfachen Import