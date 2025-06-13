Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    if (args.Length < 1 || args[0] == "--help" || args[0] == "-h")
    {
        ShowHelp();
        return;
    }

    var configPath = args[0];
    Log.Information("Starte HashBackup mit Konfiguration: {ConfigPath}", configPath);
    
    // Entferne den ersten Parameter (configPath) aus den Args für die weitere Verarbeitung
    var configArgs = args.Length > 1 ? args.Skip(1).ToArray() : [];
    var config = new ConfigLoader(configPath, configArgs);

    // Lese alle wichtigen Konfigurationswerte aus und logge sie
    var backupType = config.Get("DEFAULT", "BACKUP_TYPE")!;
    var sourceFolder = config.Get("DEFAULT", "SOURCE_FOLDER");
    if (string.IsNullOrWhiteSpace(sourceFolder))
    {
        Log.Error("Fehler: SOURCE_FOLDER ist nicht gesetzt");
        return;
    }
    
    // Prüfe und setze den Lock, damit das Backup nicht mehrfach gleichzeitig läuft
    var lockFilePath = config.Get("DEFAULT", "LOCK_FILE", Path.Combine(Path.GetTempPath(), "hashbackup.lock"))!;
    Log.Information("Verwende Lock-Datei: {LockFilePath}", lockFilePath);
    
    using var fileLock = new FileLock(lockFilePath);
    if (!fileLock.TryAcquireLock())
    {
        Log.Error("Eine andere Instanz von HashBackup läuft bereits. Beende Programm...");
        return;
    }
    
    Log.Information("Lock erfolgreich gesetzt. Starte Backup...");
    
    var metadataFile = config.Get("DEFAULT", "BACKUP_METADATA_FILE", "backup_metadata.csv");
    if (string.IsNullOrWhiteSpace(metadataFile))
    {
        Log.Error("Fehler: BACKUP_METADATA_FILE ist nicht gesetzt");
        return;
    }
    
    var parallelUploads = int.TryParse(config.Get("DEFAULT", "PARALLEL_UPLOADS"), out var pu) ? pu : 1;
    var safeMode = config.Get("DEFAULT", "SAFE_MODE", "false")?.ToLower() == "true";
    var dryRun = config.Get("DEFAULT", "DRY_RUN", "false")?.ToLower() == "true";
    var maxRetries = int.TryParse(config.Get("DEFAULT", "MAX_RETRIES"), out var mr) ? mr : 3;
    var retryDelay = int.TryParse(config.Get("DEFAULT", "RETRY_DELAY"), out var rd) ? rd : 5;
    var jobName = config.Get("DEFAULT", "JOB_NAME", "Default")!;
    var targetDirDepth = int.TryParse(config.Get("DEFAULT", "TARGET_DIR_DEPTH"), out var tdd) ? tdd : 3;

    // Logge die Konfigurationsparameter übersichtlich
    Log.Information("Konfigurationsparameter:");
    Log.Information("  Backend-Typ: {BackupType}", backupType);
    Log.Information("  Quellverzeichnis: {SourceFolder}", sourceFolder);
    Log.Information("  Metadaten-Datei: {MetadataFile}", metadataFile);
    Log.Information("  Parallele Uploads: {ParallelUploads}", parallelUploads);
    Log.Information("  Safe-Mode: {SafeMode}", safeMode);
    Log.Information("  Dry-Run: {DryRun}", dryRun);
    Log.Information("  Max. Wiederholungen: {MaxRetries}", maxRetries);
    Log.Information("  Verzögerung bei Wiederholung: {RetryDelay} Sekunden", retryDelay);
    Log.Information("  Job-Name: {JobName}", jobName);
    Log.Information("  Zielverzeichnistiefe: {TargetDirDepth}", targetDirDepth);

    // Lade ignorierte Dateien
    var ignoredFilesStr = config.Get("DEFAULT", "IGNORED_FILES", "");
    var ignoredFiles = string.IsNullOrWhiteSpace(ignoredFilesStr) 
        ? new List<string>() 
        : ignoredFilesStr.Split(',').Select(x => x.Trim()).ToList();
    
    if (ignoredFiles.Any())
    {
        Log.Information("  Ignorierte Dateien: {IgnoredFiles}", string.Join(", ", ignoredFiles));
    }
    
    // Generiere die Konfigurationsdokumentation für die CSV-Datei
    var configDoku = config.GenerateConfigDoku(configPath);

    // Erstelle das Backend
    var backend = StorageBackendFactory.Create(config);
    
    // Jetzt erst mit den Hashes arbeiten
    var hashes = await backend.FetchHashesAsync();
    Log.Information("Gefundene Hashes im Ziel: {Count}", hashes.Count);
    foreach (var kv in hashes)
    {
        Log.Debug("Hash: {Hash} -> Größe: {Size}", kv.Key, kv.Value);
    }

    var backupJob = new BackupJob(
        backend,
        sourceFolder,
        metadataFile,
        parallelUploads,
        safeMode,
        dryRun,
        maxRetries,
        retryDelay,
        jobName,
        targetDirDepth,
        configDoku,
        ignoredFiles
    );
    await backupJob.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Ein schwerwiegender Fehler ist aufgetreten");
}
finally
{
    Log.CloseAndFlush();
}

void ShowHelp()
{
    Console.WriteLine("HashBackup - Tool zum Hashbasierten Backup von Dateien");
    Console.WriteLine("Verwendung: HashBackup <config-file> [optionen]");
    Console.WriteLine();
    Console.WriteLine("Parameter:");
    Console.WriteLine("  <config-file>            Pfad zur Konfigurationsdatei (.ini oder .json)");
    Console.WriteLine("  -h, --help               Zeigt diese Hilfe an");
    Console.WriteLine();
    Console.WriteLine("Optionen:");
    Console.WriteLine("  -s, --source <path>      Quellverzeichnis für das Backup");
    Console.WriteLine("  -t, --target <path>      Zielort für das Backup");
    Console.WriteLine("  -j, --job-name <name>    Name des Backup-Jobs");
    Console.WriteLine("  -tp, --type <type>       Typ des Backups (lokal oder azure)");
    Console.WriteLine("  -m, --metadata <file>    Pfad zur Metadaten-Datei");
    Console.WriteLine("  -p, --parallel <num>     Anzahl paralleler Uploads");
    Console.WriteLine("  -sm, --safe-mode         Safe-Mode aktivieren");
    Console.WriteLine("  -d, --dry-run            Dry-Run ohne tatsächliche Änderungen");
    Console.WriteLine("  -r, --max-retries <num>  Max. Anzahl an Wiederholungen");
    Console.WriteLine("  -rd, --retry-delay <sec> Verzögerung zwischen Wiederholungen");
    Console.WriteLine("  -dd, --dir-depth <num>   Tiefe der Zielverzeichnisstruktur");
    Console.WriteLine();
    Console.WriteLine("Umgebungsvariablen:");
    Console.WriteLine("  HASHBACKUP_DEFAULT__*    Konfigurationsvariablen mit HASHBACKUP_-Prefix");
    Console.WriteLine("  Beispiel: HASHBACKUP_DEFAULT__SOURCE_FOLDER=/path/to/source");
    
    Log.Information("Hilfe zur Verwendung von HashBackup angezeigt");
}
