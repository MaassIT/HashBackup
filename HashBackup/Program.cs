Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    if (args.Length < 1)
    {
        Log.Error("Bitte Konfigurationsdatei als Argument angeben (INI oder JSON)");
        return;
    }
    var configPath = args[0];
    var config = new ConfigLoader(configPath);
    var backend = StorageBackendFactory.Create(config);

    Log.Information("Starte mit Backend: {Backend}", config.Get("DEFAULT", "BACKUP_TYPE"));
    var hashes = await backend.FetchHashesAsync();
    Log.Information("Gefundene Hashes im Ziel: {Count}", hashes.Count);
    foreach (var kv in hashes)
    {
        Log.Debug("Hash: {Hash} -> Größe: {Size}", kv.Key, kv.Value);
    }

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
