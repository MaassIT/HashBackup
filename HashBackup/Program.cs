Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    if (args.Length < 1)
    {
        Log.Error("Bitte Konfigurationsdatei als Argument angeben (INI oder JSON)");
        return;
    }
    var configPath = args[0];
    Log.Information("Starte HashBackup mit Konfiguration: {ConfigPath}", configPath);
    var config = new ConfigLoader(configPath);

    // Lese alle wichtigen Konfigurationswerte aus und logge sie
    var backupType = config.Get("DEFAULT", "BACKUP_TYPE")!;
    var sourceFolder = config.Get("DEFAULT", "SOURCE_FOLDER");
    if (string.IsNullOrWhiteSpace(sourceFolder))
    {
        Log.Error("Fehler: SOURCE_FOLDER ist nicht gesetzt");
        return;
    }
    
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
        targetDirDepth
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
