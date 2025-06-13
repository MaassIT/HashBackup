# 🔐 pyHashedBackup – Hash-basiertes, modulares Backup-Tool

**pyHashedBackup** ist ein leistungsstarkes Backup-Tool in Python, das auf **MD5-Hashes** basiert, um nur geänderte Dateien zu sichern. Durch seine **modulare Architektur** können verschiedene Backup-Ziele flexibel angebunden werden – aktuell **lokaler Speicher** und **Azure Storage**.

## 🚀 Funktionen

- ✅ **Effiziente Backups** durch MD5-Hash-Vergleich (nur geänderte Dateien werden gespeichert)
- 📂 **Unterstützte Backup-Ziele**:
  - 🖥️ **Lokaler Speicher**
  - ☁️ **Azure Blob Storage**
- 🔌 **Modular erweiterbar** für weitere Speicherziele
- 🔄 **Wiederaufnahme von fehlgeschlagenen Uploads** mit automatischem Retry
- 🛠️ **LVM Snapshot-Unterstützung** (in Planung)
- 🖥️ **Interaktive Fortschrittsanzeige** mit `curses`
- 🔒 **Locking-Mechanismus**, um parallele Backups zu verhindern

## 🏗️ Installation

Da **pyHashedBackup** keine externen Abhängigkeiten hat, ist keine Installation erforderlich. Stelle lediglich sicher, dass **Python 3.x** installiert ist.

## 📜 Verwendung

```bash
python pyHashedBackup.py -c /pfad/zur/config.ini
```

Alternativ direkt mit CLI-Argumenten:

```bash
python pyHashedBackup.py --DEFAULT.SOURCE_FOLDER=/daten --DEFAULT.BACKUP_TYPE=local --LOCAL_STORAGE.DESTINATION=/backup
```

## ⚙️ Konfigurationsdatei (Beispiel)

```ini
[DEFAULT]
BACKUP_TYPE = azure
SOURCE_FOLDER = /daten
BACKUP_METADATA_FILE = /backup/metadata.csv
SAFE_MODE = true
DRY_RUN = false
PARALLEL_UPLOADS = 3
JOB_NAME = "NightlyBackup"
LOCK_FILE = /var/lock/backup.lock
TARGET_DIR_DEPTH = 3

[AZURE]
STORAGE_ACCOUNT = meinaccount
STORAGE_KEY = geheim
CONTAINER = mein-container
```

## 📦 Backup-Ziele erweitern

Neue Backup-Ziele lassen sich durch eigene **Module** einfach integrieren.

## 🏗️ Geplante Features

- 📸 Unterstützung für **LVM Snapshots**
- 🔄 **Inkrementelle Backups** mit Datei-Versionskontrolle
- 📂 **Unterstützung für weitere Cloud-Speicher** (z.B. S3, Google Drive)

## 🛡️ Lizenz

Dieses Projekt steht unter der **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0) Lizenz**.

➡️ **Das bedeutet:**\
✅ **Freie private Nutzung**\
❌ **Kommerzielle Nutzung ist untersagt**\
📜 Mehr Informationen: [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)

