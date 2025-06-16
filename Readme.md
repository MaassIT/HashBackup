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
- 🚫 **Flexible Ignore-Patterns** für Dateien und Verzeichnisse (ähnlich .gitignore)
- 📁 **Unterstützung mehrerer Quellverzeichnisse** für kombinierte Backups
- 🔄 **Zuverlässige Wiederholungslogik** bei Netzwerkproblemen

## 🏗️ Installation

1. **.NET 9 SDK installieren** ([Download](https://dotnet.microsoft.com/download))
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
SOURCE_FOLDER = /daten,/weitere-daten,/noch-mehr-daten
BACKUP_METADATA_FILE = /backup/metadata.csv
SAFE_MODE = true
DRY_RUN = false
PARALLEL_UPLOADS = 3
JOB_NAME = NightlyBackup
LOCK_FILE = /var/lock/backup.lock
TARGET_DIR_DEPTH = 3
IGNORE = *.tmp,*.bak,.DS_Store,node_modules,.git
IGNORE_FILE = /pfad/zur/ignore_datei.txt
MAX_RETRIES = 3
RETRY_DELAY = 5

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
    "SOURCE_FOLDER": ["/daten", "/weitere-daten", "/noch-mehr-daten"],
    "BACKUP_METADATA_FILE": "/backup/metadata.csv",
    "SAFE_MODE": "true",
    "DRY_RUN": "false",
    "PARALLEL_UPLOADS": "3",
    "JOB_NAME": "NightlyBackup",
    "LOCK_FILE": "/var/lock/backup.lock",
    "TARGET_DIR_DEPTH": "3",
    "IGNORE": "*.tmp,*.bak,.DS_Store,node_modules,.git",
    "IGNORE_FILE": "/pfad/zur/ignore_datei.txt",
    "MAX_RETRIES": "3",
    "RETRY_DELAY": "5"
  },
  "AZURE": {
    "STORAGE_ACCOUNT": "meinaccount",
    "STORAGE_KEY": "geheim",
    "CONTAINER": "mein-container"
  }
}
```

### Ignorierte Dateien und Verzeichnisse konfigurieren

HashBackup bietet zwei Möglichkeiten, Dateien und Verzeichnisse vom Backup auszuschließen:

1. **Direkt in der Konfiguration** über die `IGNORE`-Einstellung mit kommagetrennten Mustern:
   ```ini
   IGNORE = *.tmp,*.log,node_modules,.git,*.bak
   ```

2. **Über eine externe Datei** ähnlich einer `.gitignore`-Datei, die in `IGNORE_FILE` angegeben wird:
   ```ini
   IGNORE_FILE = /pfad/zur/backupignore.txt
   ```

   Beispiel für den Inhalt einer Ignore-Datei:
   ```
   # Temporäre Dateien ignorieren
   *.tmp
   *.bak
   *.log
   
   # Systemdateien
   .DS_Store
   Thumbs.db
   
   # Verzeichnisse
   node_modules
   .git
   bin/Debug
   ```

3. **Über die Kommandozeile**:
   ```bash
   dotnet run --project HashBackup/HashBackup.csproj config.ini --ignore "*.tmp,*.bak,node_modules" --ignore-file "ignore.txt"
   ```

Die Muster unterstützen Wildcards wie `*` und `?`, und es wird nicht mehr zwischen Dateien und Verzeichnissen unterschieden - alle Muster werden auf beide angewendet.

## ⚙️ Erweiterbarkeit

Neue Backup-Ziele lassen sich durch Implementierung des `IStorageBackend`-Interfaces einfach integrieren.

## 📂 Projektstruktur

HashBackup ist modular aufgebaut und besteht aus folgenden Hauptkomponenten:

- **Services**: Kernfunktionalität für Dateihashing, Metadatenverwaltung und Upload-Koordination
- **Storage**: Backend-Implementierungen für verschiedene Speicherziele (lokal, Azure)
- **Utils**: Hilfsfunktionen für Dateisystemoperationen, Locking und Ignore-Muster
- **PythonTool**: Komplementäres Python-Skript für ähnliche Funktionalität in Python

## 📋 Kommandozeilen-Parameter

```bash
HashBackup <config-file> [optionen]
```

| Parameter | Beschreibung |
|-----------|-------------|
| `-s`, `--source` | Quellverzeichnis(se) für das Backup |
| `-t`, `--target` | Zielort für das Backup |
| `-j`, `--job-name` | Name des Backup-Jobs |
| `-m`, `--metadata` | Pfad zur Metadaten-Datei |
| `-p`, `--parallel` | Anzahl paralleler Uploads |
| `-sm`, `--safe-mode` | Safe-Mode aktivieren |
| `-d`, `--dry-run` | Dry-Run ohne tatsächliche Änderungen |
| `-i`, `--ignore` | Zu ignorierende Dateien/Verzeichnisse |
| `-if`, `--ignore-file` | Pfad zu einer Datei mit Ignorier-Mustern |
| `-ll`, `--log-level` | Log-Level (Verbose, Debug, Information, Warning, Error, Fatal) |
| `-r`, `--retries` | Maximale Anzahl an Wiederholungen |
| `-rd`, `--retry-delay` | Verzögerung in Sekunden zwischen Wiederholungen |

## 🏗️ Geplante Features

- 📸 Unterstützung für LVM Snapshots
- 🔄 Inkrementelle Backups mit Datei-Versionskontrolle
- 📂 Unterstützung für weitere Cloud-Speicher (z.B. S3, Google Drive)
- 🖥️ Interaktive Fortschrittsanzeige (wie im Python-Original)

## 🛡️ Lizenz

Dieses Projekt verwendet ein **Dual-Licensing-Modell**:

### 🏠 Kostenlose Variante
- **Für private Nutzung und nicht-kommerzielle Zwecke**
- Unter der **GNU Affero General Public License v3 (AGPL-3.0)**
- Alle Änderungen müssen öffentlich zugänglich gemacht werden

### 🏢 Kommerzielle Variante
- **Für Unternehmen und kommerzielle Anwendungen**
- Kostenpflichtige Lizenz erforderlich
- Kontaktiere mich für Lizenzoptionen und Preise
- Bietet zusätzliche Funktionen und professionellen Support

➡️ Die kostenlose Version darf in Unternehmen **nicht** ohne entsprechende kommerzielle Lizenz eingesetzt werden.

📜 Mehr Informationen: [GNU AGPL-3.0](https://www.gnu.org/licenses/agpl-3.0.html)

---

**Hinweis:**
Für Fragen, Feature-Wünsche oder Bugreports bitte ein Issue auf Github eröffnen.
