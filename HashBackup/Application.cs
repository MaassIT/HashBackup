using HashBackup.Commands;
using HashBackup.Recovery;

namespace HashBackup;

/// <summary>
/// Hauptanwendungsklasse für HashBackup
/// </summary>
public class Application(string[] args)
{
    private ConfigLoader? _config;
    private BackupConfiguration? _backupConfig;

    /// <summary>
    /// Führt die Anwendung aus
    /// </summary>
    public async Task<int> RunAsync()
    {
        try
        {
            // Hilfe anzeigen, wenn keine Argumente vorhanden sind oder Hilfe angefordert wird
            if (args.Length < 1 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                ShowHelp();
                return 0;
            }

            var commandLine = CommandLineParser.Parse(args);
            Log.Information(
                "Starte HashBackup-Befehl {Command} mit Konfiguration: {ConfigPath}",
                commandLine.Command,
                commandLine.ConfigPath);
            _config = new ConfigLoader(commandLine.ConfigPath, commandLine.ConfigArguments);

            // Rekonfiguriere den Logger mit dem konfigurierten Log-Level
            var logLevel = _config.Get("DEFAULT", "LOG_LEVEL", "Information")!;
            ConfigureLogger(logLevel);

            // Backup-Konfiguration laden
            _backupConfig = LoadBackupConfiguration(_config);
            if (_backupConfig == null)
            {
                throw new InvalidOperationException("Die Backup-Konfiguration ist ungültig.");
            }

            // Backup-Konfiguration protokollieren
            LogBackupConfiguration(_backupConfig);

            // Erstelle das Backend. Restore und Verify benötigen zusätzlich den
            // IReadableStorageBackend-Vertrag, bestehende Backup-Backends bleiben kompatibel.
            var backend = StorageBackendFactory.Create(_config);
            if (commandLine.Command != HashBackupCommand.Backup)
            {
                return await RunRecoveryCommandAsync(commandLine, backend, _backupConfig);
            }

            // Prüfe und setze den Lock, damit das Backup nicht mehrfach gleichzeitig läuft
            using var fileLock = new FileLock(_backupConfig.LockFilePath);
            if (!fileLock.TryAcquireLock())
            {
                Log.Error("Eine andere Instanz von HashBackup läuft bereits. Beende Programm...");
                // Ein bereits laufender Cron-Job wurde historisch als sauberer Skip
                // behandelt. Dieser Exitcode bleibt für bestehende Skripte kompatibel.
                return 0;
            }

            Log.Information("Lock erfolgreich gesetzt. Starte Backup...");

            // Generiere die Konfigurationsdokumentation für die CSV-Datei
            var configDoku = _config.GenerateConfigDoku(commandLine.ConfigPath);

            // Erstelle und führe Backup-Job aus
            var backupJob = CreateBackupJob(backend, _backupConfig, configDoku);
            await backupJob.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Ein schwerwiegender Fehler ist aufgetreten");
            throw;
        }
    }

    private static async Task<int> RunRecoveryCommandAsync(
        CommandLineRequest commandLine,
        IStorageBackend backend,
        BackupConfiguration config)
    {
        if (backend is not IReadableStorageBackend readableBackend)
        {
            throw new NotSupportedException("Das konfigurierte Storage-Backend unterstützt Restore/Verify nicht.");
        }

        var options = new RecoveryOptions(
            commandLine.MetadataReference,
            commandLine.Destination,
            commandLine.DeepVerify,
            commandLine.Rehydrate,
            commandLine.RehydrateTier,
            commandLine.RehydratePriority,
            commandLine.Overwrite,
            commandLine.DryRun || config.DryRun);
        var recoveryService = new RecoveryService(
            readableBackend,
            config.TargetDirDepth,
            config.SourceFolders,
            config.JobName);

        var result = commandLine.Command switch
        {
            HashBackupCommand.Verify => await recoveryService.VerifyAsync(options),
            HashBackupCommand.Restore => await recoveryService.RestoreAsync(options),
            _ => throw new ArgumentOutOfRangeException(nameof(commandLine), commandLine.Command, null)
        };

        if (result.Status == RecoveryStatus.RehydrationPending)
        {
            Log.Warning(
                "Archive-Daten sind noch nicht online. Befehl später unverändert erneut ausführen. Exitcode 2 signalisiert ausstehende Rehydration.");
        }

        return (int)result.Status;
    }

    /// <summary>
    /// Erstellt eine neue BackupJob-Instanz basierend auf der aktuellen Konfiguration
    /// </summary>
    private BackupJob CreateBackupJob(IStorageBackend backend, BackupConfiguration config, IEnumerable<string> configDoku)
    {
        // Erstelle den BackupJob mit der Konfiguration und den Dokumentationszeilen
        return new BackupJob(backend, config, configDoku);
    }

    /// <summary>
    /// Zeigt die Hilfe-Informationen
    /// </summary>
    private static void ShowHelp()
    {
        Console.WriteLine("HashBackup - Tool zum Hashbasierten Backup von Dateien");
        Console.WriteLine("Verwendung:");
        Console.WriteLine("  HashBackup <config-file> [backup-optionen]       # kompatible Backup-Syntax");
        Console.WriteLine("  HashBackup backup <config-file> [optionen]");
        Console.WriteLine("  HashBackup verify <config-file> [optionen]");
        Console.WriteLine("  HashBackup restore <config-file> --destination <pfad> [optionen]");
        Console.WriteLine();
        Console.WriteLine("Parameter:");
        Console.WriteLine("  <config-file>            Pfad zur Konfigurationsdatei (.ini oder .json)");
        Console.WriteLine("  -h, --help               Zeigt diese Hilfe an");
        Console.WriteLine();
        Console.WriteLine("Optionen:");
        Console.WriteLine("  -s, --source <path>      Quellverzeichnis für das Backup");
        Console.WriteLine("  -t, --target <path>      Zielort für das Backup");
        Console.WriteLine("  -j, --job-name <n>       Name des Backup-Jobs");
        Console.WriteLine("  -tp, --type <type>       Typ des Backups (lokal oder azure)");
        Console.WriteLine("  -m, --metadata <file>    Pfad zur Metadaten-Datei");
        Console.WriteLine("  -p, --parallel <num>     Anzahl paralleler Uploads");
        Console.WriteLine("  -sm, --safe-mode         Safe-Mode aktivieren");
        Console.WriteLine("  -d, --dry-run            Dry-Run ohne Quell- oder Zieldatenänderung");
        Console.WriteLine("  -r, --max-retries <num>  Max. Anzahl an Wiederholungen");
        Console.WriteLine("  -rd, --retry-delay <sec> Verzögerung zwischen Wiederholungen");
        Console.WriteLine("  -dd, --dir-depth <num>   Tiefe der Zielverzeichnisstruktur");
        Console.WriteLine("  -ll, --log-level <level> Log-Level (Verbose, Debug, Information, Warning, Error, Fatal)");
        Console.WriteLine("  -i, --ignore <patterns>  Zu ignorierende Dateien/Verzeichnisse (z.B. *.tmp,node_modules)");
        Console.WriteLine("  -if, --ignore-file <file> Pfad zu einer Datei mit Ignorier-Mustern (ähnlich .gitignore)");
        Console.WriteLine();
        Console.WriteLine("Restore-/Verify-Optionen:");
        Console.WriteLine("  -m, --metadata <ref>     Lokale CSV, Blob-Pfad oder 'latest' (Standard)");
        Console.WriteLine("  -o, --destination <pfad> Restore-Zielverzeichnis");
        Console.WriteLine("  --deep                   Inhalte herunterladen und MD5 vollständig prüfen");
        Console.WriteLine("  --rehydrate              Offline-Blobs kostenpflichtig rehydrieren");
        Console.WriteLine("  --rehydrate-tier <tier>  Ziel-Tier: hot, cool (Standard) oder cold");
        Console.WriteLine("  --rehydrate-priority <p> standard (Standard) oder high");
        Console.WriteLine("  --overwrite              Abweichende vorhandene Restore-Dateien ersetzen");
        Console.WriteLine();
        Console.WriteLine("Umgebungsvariablen:");
        Console.WriteLine("  HASHBACKUP_DEFAULT__*    Konfigurationsvariablen mit HASHBACKUP_-Prefix");
        Console.WriteLine("  Beispiel: HASHBACKUP_DEFAULT__SOURCE_FOLDER=/path/to/source");

        Log.Information("Hilfe zur Verwendung von HashBackup angezeigt");
    }

    /// <summary>
    /// Konfiguriert den Logger mit dem angegebenen Log-Level
    /// </summary>
    private void ConfigureLogger(string logLevelString)
    {
        var logLevel = ParseLogLevel(logLevelString);

        // Logger mit SensitiveDataDestructurer konfigurieren, um sensible Daten zu maskieren
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext();

        // Logger mit SensitiveDataDestructurer einrichten
        var logger = loggerConfig.CreateLogger();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .WriteTo.Sink(new SensitiveDataDestructurer(logger))
            .CreateLogger();

        Log.Information("Logger mit Log-Level {LogLevel} und Schutz sensibler Daten konfiguriert", logLevelString);
    }

    /// <summary>
    /// Konvertiert einen String in ein LogEventLevel
    /// </summary>
    private LogEventLevel ParseLogLevel(string logLevelString)
    {
        return logLevelString.ToLower() switch
        {
            "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" => LogEventLevel.Information,
            "info" => LogEventLevel.Information,
            "warning" => LogEventLevel.Warning,
            "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information // Standard ist Information
        };
    }

    /// <summary>
    /// Lädt die Backup-Konfiguration aus dem Config-Loader
    /// </summary>
    private BackupConfiguration? LoadBackupConfiguration(ConfigLoader config)
    {
        // Lade den Backend-Typ
        var backupType = config.Get("DEFAULT", "BACKUP_TYPE");
        if (string.IsNullOrWhiteSpace(backupType))
        {
            Log.Error("Fehler: BACKUP_TYPE ist nicht gesetzt");
            return null;
        }

        // Unterstütze mehrere Quellordner
        var sourceFolderString = config.Get("DEFAULT", "SOURCE_FOLDERS", string.Empty);
        if (string.IsNullOrWhiteSpace(sourceFolderString))
        {
            sourceFolderString = config.Get("DEFAULT", "SOURCE_FOLDER");
        }
        if (string.IsNullOrWhiteSpace(sourceFolderString))
        {
            Log.Error("Fehler: Weder SOURCE_FOLDERS noch SOURCE_FOLDER ist gesetzt");
            return null;
        }

        // Parsen der Quellordner aus der Konfiguration (kommagetrennte Liste)
        var sourceFolders = sourceFolderString.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => folder.Trim())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .ToList();

        if (sourceFolders.Count == 0)
        {
            Log.Error("Fehler: Keine gültigen Quellordner angegeben");
            return null;
        }

        var lockFilePath = config.Get("DEFAULT", "LOCK_FILE", Path.Combine(Path.GetTempPath(), "hashbackup.lock"))!;

        var metadataFile = config.Get("DEFAULT", "BACKUP_METADATA_FILE", "backup_metadata.csv");
        if (string.IsNullOrWhiteSpace(metadataFile))
        {
            Log.Error("Fehler: BACKUP_METADATA_FILE ist nicht gesetzt");
            return null;
        }

        // Parse numerische Werte mit Standardwerten
        var parallelUploads = int.TryParse(config.Get("DEFAULT", "PARALLEL_UPLOADS", "1"), out var pu) ? pu : 1;
        var safeMode = config.Get("DEFAULT", "SAFE_MODE", "false")?.ToLower() == "true";
        var dryRun = config.Get("DEFAULT", "DRY_RUN", "false")?.ToLower() == "true";
        var maxRetries = int.TryParse(config.Get("DEFAULT", "MAX_RETRIES", "3"), out var mr) ? mr : 3;
        var retryDelay = int.TryParse(config.Get("DEFAULT", "RETRY_DELAY", "5"), out var rd) ? rd : 5;
        var jobName = config.Get("DEFAULT", "JOB_NAME", "Default")!;
        var targetDirDepth = int.TryParse(config.Get("DEFAULT", "TARGET_DIR_DEPTH", "3"), out var tdd) ? tdd : 3;

        if (parallelUploads is < 1 or > 16 ||
            maxRetries is < 1 or > 20 ||
            retryDelay is < 0 or > 3600 ||
            targetDirDepth is < 1 or > 8)
        {
            Log.Error(
                "Ungültige numerische Backup-Konfiguration: PARALLEL_UPLOADS={ParallelUploads}, MAX_RETRIES={MaxRetries}, RETRY_DELAY={RetryDelay}, TARGET_DIR_DEPTH={TargetDirDepth}",
                parallelUploads,
                maxRetries,
                retryDelay,
                targetDirDepth);
            return null;
        }

        // Lade Ignorier-Muster (kommagetrennt)
        var ignorePatternString = config.Get("DEFAULT", "IGNORE", string.Empty);
        var ignorePatterns = string.IsNullOrWhiteSpace(ignorePatternString)
            ? new List<string>()
            : ignorePatternString.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(pattern => pattern.Trim())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .ToList();

        // Pfad zur externen Datei mit Ignorier-Mustern
        var ignoreFilePath = config.Get("DEFAULT", "IGNORE_FILE", string.Empty);

        // Wenn eine externe Ignorier-Datei angegeben wurde und existiert, lade die Muster
        if (!string.IsNullOrWhiteSpace(ignoreFilePath) && File.Exists(ignoreFilePath))
        {
            try
            {
                // Lese die Ignorier-Datei und ergänze die Muster
                var fileIgnorePatterns = File.ReadAllLines(ignoreFilePath)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                    .ToArray();

                ignorePatterns.AddRange(fileIgnorePatterns);

                Log.Information("Ignorier-Muster aus Datei {Path} geladen: {Count} Muster",
                    ignoreFilePath, fileIgnorePatterns.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Laden der Ignorier-Datei {Path}", ignoreFilePath);
            }
        }

        Log.Information("Konfiguration geladen: {JobName}, {SourceCount} Quellordner, {IgnorePatternCount} Ignorier-Muster",
            jobName, sourceFolders.Count, ignorePatterns.Count);

        return new BackupConfiguration
        {
            BackupType = backupType,
            SourceFolders = sourceFolders,
            LockFilePath = lockFilePath,
            MetadataFile = metadataFile,
            ParallelUploads = parallelUploads,
            SafeMode = safeMode,
            DryRun = dryRun,
            MaxRetries = maxRetries,
            RetryDelay = retryDelay,
            JobName = jobName,
            TargetDirDepth = targetDirDepth,
            IgnorePatterns = ignorePatterns,
            IgnoreFile = ignoreFilePath
        };
    }

    /// <summary>
    /// Loggt die Backup-Konfiguration
    /// </summary>
    private void LogBackupConfiguration(BackupConfiguration config)
    {
        // Logge die Konfigurationsparameter übersichtlich
        Log.Information("Konfigurationsparameter:");
        Log.Information("  Backend-Typ: {BackupType}", config.BackupType);

        // Zeige alle Quellordner im Log
        if (config.SourceFolders.Count == 1)
        {
            Log.Information("  Quellverzeichnis: {SourceFolder}", config.SourceFolders[0]);
        }
        else
        {
            Log.Information("  Quellverzeichnisse:");
            foreach (var folder in config.SourceFolders)
            {
                Log.Information("    - {SourceFolder}", folder);
            }
        }

        Log.Information("  Lock-Datei: {LockFilePath}", config.LockFilePath);
        Log.Information("  Metadaten-Datei: {MetadataFile}", config.MetadataFile);
        Log.Information("  Parallele Uploads: {ParallelUploads}", config.ParallelUploads);
        Log.Information("  Safe-Mode: {SafeMode}", config.SafeMode);
        Log.Information("  Dry-Run: {DryRun}", config.DryRun);
        Log.Information("  Max. Wiederholungen: {MaxRetries}", config.MaxRetries);
        Log.Information("  Verzögerung bei Wiederholung: {RetryDelay} Sekunden", config.RetryDelay);
        Log.Information("  Job-Name: {JobName}", config.JobName);
        Log.Information("  Zielverzeichnistiefe: {TargetDirDepth}", config.TargetDirDepth);

        // Ignorierte Muster anzeigen
        if (config.IgnorePatterns != null && config.IgnorePatterns.Count != 0)
        {
            Log.Information("  Ignorierte Muster: {IgnorePatterns}", string.Join(", ", config.IgnorePatterns));
        }
    }
}
