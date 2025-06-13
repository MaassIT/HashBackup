# 🔐 HashBackup – Hash-basiertes, modulares Backup-Tool in C#

**HashBackup** ist ein leistungsstarkes, modulares Backup-Tool in C#, das auf MD5-Hashes basiert, um nur geänderte Dateien effizient zu sichern. Es unterstützt lokale Backups und Azure Blob Storage als Ziel und ist für den produktiven Einsatz auf Linux, macOS und Windows ausgelegt.

## 🚀 Features

- ✅ **Effiziente Backups** durch MD5-Hash-Vergleich (nur geänderte Dateien werden gesichert)
- 📂 **Unterstützte Backup-Ziele**:
  - 🖥️ **Lokaler Speicher** (mit xattr/ADS für Metadaten)
  - ☁️ **Azure Blob Storage**
- 🔌 **Modular erweiterbar** (Storage-Backend-Architektur)
- 🔄 **Wiederaufnahme von fehlgeschlagenen Uploads** mit automatischem Retry
- 🛠️ **Plattformübergreifend** (Linux/macOS: xattr, Windows: NTFS ADS)
- 🖥️ **Parallele Uploads** für hohe Performance
- 🔒 **Locking-Mechanismus**, um parallele Backups zu verhindern
- 📝 **Backup-Metadaten als CSV**
- 🏗️ **Konfigurierbar per INI oder JSON**

## 🏗️ Installation

1. **.NET 6/7/8/9 SDK installieren** ([Download](https://dotnet.microsoft.com/download))
2. Repository klonen:
   ```bash
   git clone https://github.com/deinuser/HashBackup.git
   cd HashBackup
   ```
3. Abhängigkeiten installieren:
   ```bash
   dotnet restore
   ```
4. Build:
   ```bash
   dotnet build
   ```

## 📜 Verwendung

```bash
dotnet run --project HashBackup/HashBackup.csproj /pfad/zur/backup_config.ini
```

Oder mit JSON-Konfiguration:

```bash
dotnet run --project HashBackup/HashBackup.csproj /pfad/zur/backup_config.json
```

### Beispiel-Konfigurationsdatei (INI)

```ini
[DEFAULT]
BACKUP_TYPE = azure
SOURCE_FOLDER = /daten
BACKUP_METADATA_FILE = /backup/metadata.csv
SAFE_MODE = true
DRY_RUN = false
PARALLEL_UPLOADS = 3
JOB_NAME = NightlyBackup
LOCK_FILE = /var/lock/backup.lock
TARGET_DIR_DEPTH = 3

[AZURE]
STORAGE_ACCOUNT = meinaccount
STORAGE_KEY = geheim
CONTAINER = mein-container
```

### Beispiel-Konfigurationsdatei (JSON)

```json
{
  "DEFAULT": {
    "BACKUP_TYPE": "azure",
    "SOURCE_FOLDER": "/daten",
    "BACKUP_METADATA_FILE": "/backup/metadata.csv",
    "SAFE_MODE": "true",
    "DRY_RUN": "false",
    "PARALLEL_UPLOADS": "3",
    "JOB_NAME": "NightlyBackup",
    "LOCK_FILE": "/var/lock/backup.lock",
    "TARGET_DIR_DEPTH": "3"
  },
  "AZURE": {
    "STORAGE_ACCOUNT": "meinaccount",
    "STORAGE_KEY": "geheim",
    "CONTAINER": "mein-container"
  }
}
```

## ⚙️ Erweiterbarkeit

Neue Backup-Ziele lassen sich durch Implementierung des `IStorageBackend`-Interfaces einfach integrieren.

## 🏗️ Geplante Features

- 📸 Unterstützung für LVM Snapshots
- 🔄 Inkrementelle Backups mit Datei-Versionskontrolle
- 📂 Unterstützung für weitere Cloud-Speicher (z.B. S3, Google Drive)
- 🖥️ Interaktive Fortschrittsanzeige (wie im Python-Original)

## 🛡️ Lizenz

Dieses Projekt steht unter der **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0) Lizenz**.

➡️ **Das bedeutet:**
- ✅ Freie private Nutzung
- ❌ Kommerzielle Nutzung ist untersagt
- 📜 Mehr Informationen: [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)

---

**Hinweis:**
Dieses Tool ist die C#-Portierung des bewährten Python-Backups und wird aktiv weiterentwickelt. Für Fragen, Feature-Wünsche oder Bugreports bitte ein Issue auf Github eröffnen.

