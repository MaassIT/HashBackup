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
- 🧾 **Korrekte CSV-Metadaten** auch bei Kommas, Anführungszeichen und Zeilenumbrüchen in Dateinamen
- 0️⃣ **Sicherung leerer Dateien**, damit Marker- und Platzhalterdateien nicht verloren gehen
- 🛡️ **Integritätsprüfung bei veränderlichen Quellen** ohne falschen Sicherungszeitstempel
- 🧪 **Dry-Run ohne Änderungen an Quell- oder Zieldaten** für sichere Produktionsprüfungen
- 🔎 **Verify-Befehl** für schnelle Storage-Metadatenprüfung oder vollständigen Downloadtest
- ♻️ **Sicherer Restore** mit atomaren Dateien, MD5-Prüfung und Symlink-Schutz
- 🧊 **Archive-Rehydration** mit expliziter Kostenfreigabe, Statuscode und Wiederaufnahme

## 🏗️ Installation

1. **.NET 10 LTS SDK installieren** ([Download](https://dotnet.microsoft.com/download/dotnet/10.0))
2. Repository klonen:
   ```bash
   git clone https://github.com/MaassIT/HashBackup.git
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

Für die fertigen lokalen macOS- und Linux-x64-Binärdateien:

```bash
./build.sh
```

Die Artefakte landen anschließend unter `builds/HashBackup-macos` und
`builds/HashBackup-linux`.

## 📜 Verwendung

```bash
dotnet run --project HashBackup/HashBackup.csproj /pfad/zur/backup_config.ini
```

Die alte Syntax bleibt ein normaler Backup-Aufruf. Alternativ kann der Befehl
explizit angegeben werden:

```bash
HashBackup backup /pfad/zur/backup_config.ini -sm
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
CONTAINER = mein-container
STORAGE_TIER = Archive
```

### Beispiel-Konfigurationsdatei (JSON)

```json
{
  "DEFAULT": {
    "BACKUP_TYPE": "azure",
    "SOURCE_FOLDER": "/daten,/weitere-daten,/noch-mehr-daten",
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
    "CONTAINER": "mein-container",
    "STORAGE_TIER": "Archive"
  }
}
```

### Secrets sicher übergeben

Azure-Schlüssel sollten nicht in einer versionierten INI-/JSON-Datei stehen. HashBackup
unterstützt weiterhin die bisherigen Konfigurationsdateien, bevorzugt für Secrets aber
die bereits kompatible Umgebungsvariable:

```bash
export HASHBACKUP_AZURE__STORAGE_KEY='<Azure-Storage-Key>'
HashBackup /pfad/zur/backup_config.ini
```

Die Umgebungsvariable überschreibt den Wert aus der Konfigurationsdatei. Sie darf nicht
in Shell-History, Logs oder Repository-Dateien gespeichert werden.

### Zuverlässigkeits- und Kompatibilitätshinweise

- Bestehende CLI-Parameter und Konfigurationsschlüssel bleiben unterstützt.
- `MAX_RETRIES` bleibt kompatibel und bezeichnet zusätzliche Wiederholungen nach dem
  ersten Upload-Versuch.
- Ändert sich eine Datei während oder direkt nach dem Upload, wird die neuere Version
  nicht fälschlich als gesichert markiert. Der Lauf endet unvollständig und der nächste
  Lauf bewertet die Datei erneut.
- Ein fehlgeschlagener Metadaten-Upload lässt den gesamten Lauf mit einem Exitcode
  ungleich null fehlschlagen, weil die Metadaten für eine Wiederherstellung erforderlich
  sind.
- `RETENTION_DAYS` wird vom Programm derzeit nicht aktiv umgesetzt. Aufbewahrung und
  Löschung müssen als Azure-Lifecycle-Regel konfiguriert werden.

## 🔎 Backup prüfen

Ohne `--deep` prüft HashBackup alle referenzierten Objekte anhand von Existenz,
Dateigröße und dem vom Storage gelieferten Content-MD5. Diese Prüfung funktioniert
auch für Azure-Blobs im Archive-Tier, weil deren Eigenschaften weiterhin online
lesbar sind:

```bash
HashBackup verify /pfad/zur/backup_config.ini --metadata latest
```

Bei älteren Blobs kann das Storage-Content-MD5 fehlen. HashBackup wertet eine
erfolgreiche Existenz- und Größenprüfung dann nicht als Schaden, weist diese Objekte
aber gesammelt als **nur strukturell geprüft** aus. Der Befehl bleibt erfolgreich;
erst `--deep` bestätigt den Inhalt dieser Objekte durch einen eigenen MD5-Lauf.
Dadurch bleiben flache Prüfungen auch für Archive ohne kostenpflichtige Rehydration
nutzbar, ohne einen kryptografischen Nachweis vorzutäuschen.

Eine vollständige Prüfung lädt jedes Objekt herunter und berechnet den MD5 erneut:

```bash
HashBackup verify /pfad/zur/backup_config.ini --metadata latest --deep
```

Sind dafür benötigte Daten archiviert, endet der Befehl mit Exitcode `2`, ohne
automatisch Kosten auszulösen. Die Rehydration muss ausdrücklich angefordert werden:

```bash
HashBackup verify /pfad/zur/backup_config.ini \
  --metadata latest \
  --deep \
  --rehydrate \
  --rehydrate-tier cool \
  --rehydrate-priority standard
```

## ♻️ Backup wiederherstellen

```bash
HashBackup restore /pfad/zur/backup_config.ini \
  --metadata latest \
  --destination /pfad/zum/restore
```

Vor dem ersten Download prüft HashBackup **alle** benötigten Objekte. Wenn Archive-
Blobs enthalten sind, wird kein Teil-Restore angelegt. Mit `--rehydrate` werden die
fehlenden Online-Kopien angefordert und der Prozess endet mit Exitcode `2`:

```bash
HashBackup restore /pfad/zur/backup_config.ini \
  --metadata latest \
  --destination /pfad/zum/restore \
  --rehydrate \
  --rehydrate-tier cool \
  --rehydrate-priority standard
```

Azure-Archive-Rehydration kann mit Standardpriorität mehrere Stunden dauern. Danach
wird derselbe Restore-Befehl erneut ausgeführt. `high` kann schneller sein, verursacht
aber höhere Kosten. Eine bereits laufende Standard-Rehydration kann auf `high`
hochgestuft, jedoch nicht wieder zurückgestuft werden.

Restore-Dateien werden zunächst unter einem zufälligen temporären Namen geladen,
anschließend auf Größe und MD5 geprüft und erst danach atomar veröffentlicht.
Vorhandene korrekte Dateien werden übernommen; abweichende Dateien erfordern
`--overwrite`. Bei einer Quelle wird deren Inhalt direkt im Restore-Ziel abgelegt. Bei
mehreren `SOURCE_FOLDERS` erhält jede Quelle ein eigenes `source-N-...`-Unterverzeichnis.
Das Restore-Ziel selbst darf kein Symlink sein und wird auf Unix-Systemen bei einer
Neuanlage zunächst nur für den aktuellen Benutzer zugänglich gemacht. Ein Restore ist
pro Datei atomar und wiederaufnehmbar: Wenn ein späterer Download fehlschlägt, bleiben
bereits verifizierte Dateien erhalten und werden beim nächsten Lauf übersprungen. Eine
globale Transaktion bzw. ein Rollback über den gesamten Datenbestand findet bewusst
nicht statt, da dies bei sehr großen Backups den doppelten Speicherplatz erfordern würde.

Rehydrationsanforderungen für große Archive werden in Azure-Batches von maximal 256
Objekten gesendet. Dadurch bleiben auch umfangreiche Restore-Vorprüfungen praktikabel.

`--metadata` akzeptiert:

- `latest` (Standard): neueste Metadatendatei für `JOB_NAME` im Storage
- einen Blob-/Objektpfad wie `metadata/Job/2026/07/backup_....csv`
- den Pfad zu einer bereits lokal vorhandenen Metadaten-CSV

### Exitcodes

| Exitcode | Bedeutung |
|----------|-----------|
| `0` | Befehl erfolgreich abgeschlossen |
| `1` | Konfigurations-, Integritäts-, Download- oder Restore-Fehler |
| `2` | Archive-Rehydration erforderlich oder noch nicht abgeschlossen |

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
HashBackup backup <config-file> [optionen]
HashBackup verify <config-file> [optionen]
HashBackup restore <config-file> --destination <pfad> [optionen]
```

| Parameter | Beschreibung |
|-----------|-------------|
| `-s`, `--source` | Quellverzeichnis(se) für das Backup |
| `-t`, `--target` | Zielort für das Backup |
| `-j`, `--job-name` | Name des Backup-Jobs |
| `-m`, `--metadata` | Pfad zur Metadaten-Datei |
| `-p`, `--parallel` | Anzahl paralleler Uploads |
| `-sm`, `--safe-mode` | Safe-Mode aktivieren |
| `-d`, `--dry-run` | Dry-Run ohne Änderungen an Quell- oder Zieldaten |
| `-i`, `--ignore` | Zu ignorierende Dateien/Verzeichnisse |
| `-if`, `--ignore-file` | Pfad zu einer Datei mit Ignorier-Mustern |
| `-ll`, `--log-level` | Log-Level (Verbose, Debug, Information, Warning, Error, Fatal) |
| `-r`, `--retries` | Maximale Anzahl an Wiederholungen |
| `-rd`, `--retry-delay` | Verzögerung in Sekunden zwischen Wiederholungen |
| `-m`, `--metadata` | Bei Verify/Restore: lokale CSV, Objektpfad oder `latest` |
| `-o`, `--destination` | Zielverzeichnis für Restore |
| `--deep` | Vollständiger Verify per Download und MD5 |
| `--rehydrate` | Archive-Rehydration ausdrücklich anfordern |
| `--rehydrate-tier` | Online-Ziel: `hot`, `cool` oder `cold` |
| `--rehydrate-priority` | `standard` oder `high` |
| `--overwrite` | Abweichende vorhandene Restore-Dateien ersetzen |

## ✅ Continuous Integration

GitHub Actions prüft bei Pull Requests und Branch-Pushes:

- reproduzierbare Wiederherstellung mit .NET 10
- `dotnet format --verify-no-changes`
- warnungsfreien Release-Build
- vollständige Testsuite
- NuGet-Audit direkter und transitiver Abhängigkeiten
- Linux-x64-NativeAOT-Publish als 14 Tage verfügbares Workflow-Artefakt

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
