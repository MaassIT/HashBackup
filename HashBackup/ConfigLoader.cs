using Microsoft.Extensions.Configuration;

namespace HashBackup;

public class ConfigLoader
{
    private IConfiguration Configuration { get; set; }
    
    // Dictionary zur Verfolgung der Konfigurationsquellen
    private Dictionary<string, string> ConfigSources { get; set; }
    
    private const string SourceFile = "Konfigurationsdatei";
    private const string SourceEnv = "Umgebungsvariable";
    private const string SourceCmdline = "Kommandozeile";
    private const string SourceDefault = "Standardwert";

    public ConfigLoader(string configPath, string[] args)
    {
        Log.Debug("Lade Konfiguration aus Datei: {ConfigPath}", configPath);
        ConfigSources = new Dictionary<string, string>();
            
        // Konfigurationsbuilder erstellen
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException())
            .SetFileLoadExceptionHandler(context => 
            {
                Log.Error(context.Exception, "Fehler beim Laden der Konfigurationsdatei {Path}", 
                    context.Provider.Source.Path);
            });
        
        // Erstelle separate Konfigurationsobjekte für jede Quelle,
        // um die Quelle der Werte verfolgen zu können
        IConfiguration? fileConfig;
            
        // Je nach Dateierweiterung den passenden Provider verwenden
        if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("JSON-Konfigurationsprovider konfiguriert für {Path}", configPath);
            
            // Separate Konfiguration nur für die Datei
            var fileBuilder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath)!)
                .AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: false);
            fileConfig = fileBuilder.Build();
        }
        else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddIniFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("INI-Konfigurationsprovider konfiguriert für {Path}", configPath);
            
            // Separate Konfiguration nur für die Datei
            var fileBuilder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath)!)
                .AddIniFile(Path.GetFileName(configPath), optional: false, reloadOnChange: false);
            fileConfig = fileBuilder.Build();
        }
        else
        {
            throw new ArgumentException("Nur .ini oder .json werden unterstützt.");
        }
        
        // Speichere alle Werte aus der Konfigurationsdatei im ConfigSources-Dictionary
        foreach (var section in fileConfig.GetChildren())
        {
            foreach (var item in section.AsEnumerable().Where(x => x.Value != null))
            {
                ConfigSources[item.Key] = SourceFile;
            }
        }
        
        // Umgebungsvariablen mit Prefix hinzufügen (höhere Priorität als Konfigurationsdatei)
        builder.AddEnvironmentVariables("HASHBACKUP_");
        Log.Debug("Umgebungsvariablen mit Prefix 'HASHBACKUP_' zur Konfiguration hinzugefügt");
        
        // Separate Konfiguration für Umgebungsvariablen
        var envBuilder = new ConfigurationBuilder().AddEnvironmentVariables("HASHBACKUP_");
        var envConfig = envBuilder.Build();
        
        // Speichere Umgebungsvariablen im ConfigSources-Dictionary
        foreach (var item in envConfig.AsEnumerable().Where(x => x.Value != null))
        {
            ConfigSources[item.Key] = SourceEnv;
        }
        
        // Kommandozeilenargumente hinzufügen (höchste Priorität)
        if (args.Length > 0)
        {
            // Verarbeite Flag-Parameter speziell, die ohne Wert angegeben werden können
            var processedArgs = PreprocessCommandLineArgs(args);
            
            builder.AddCommandLine(processedArgs, GetCommandLineMapping());
            Log.Debug("Kommandozeilenargumente zur Konfiguration hinzugefügt");
            
            // Separate Konfiguration für Kommandozeilenargumente
            var cmdBuilder = new ConfigurationBuilder().AddCommandLine(processedArgs, GetCommandLineMapping());
            var cmdConfig = cmdBuilder.Build();
            
            // Speichere Kommandozeilenargumente im ConfigSources-Dictionary
            foreach (var item in cmdConfig.AsEnumerable().Where(x => x.Value != null))
            {
                ConfigSources[item.Key] = SourceCmdline;
            }
        }
            
        // Konfiguration bauen
        Configuration = builder.Build();
        
        Log.Information("Konfiguration erfolgreich geladen");
    }

    // Dictionary für Mapping von Kurzoptionen zu Konfigurationsschlüsseln
    private static Dictionary<string, string> GetCommandLineMapping()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "-s", "DEFAULT:SOURCE_FOLDER" },
            { "--source", "DEFAULT:SOURCE_FOLDER" },
            { "-t", "DEFAULT:TARGET" },
            { "--target", "DEFAULT:TARGET" },
            { "-j", "DEFAULT:JOB_NAME" },
            { "--job-name", "DEFAULT:JOB_NAME" },
            { "-tp", "DEFAULT:BACKUP_TYPE" },
            { "--type", "DEFAULT:BACKUP_TYPE" },
            { "-m", "DEFAULT:BACKUP_METADATA_FILE" },
            { "--metadata", "DEFAULT:BACKUP_METADATA_FILE" },
            { "-p", "DEFAULT:PARALLEL_UPLOADS" },
            { "--parallel", "DEFAULT:PARALLEL_UPLOADS" },
            { "-sm", "DEFAULT:SAFE_MODE" },
            { "--safe-mode", "DEFAULT:SAFE_MODE" },
            { "-d", "DEFAULT:DRY_RUN" },
            { "--dry-run", "DEFAULT:DRY_RUN" },
            { "-r", "DEFAULT:MAX_RETRIES" },
            { "--max-retries", "DEFAULT:MAX_RETRIES" },
            { "-rd", "DEFAULT:RETRY_DELAY" },
            { "--retry-delay", "DEFAULT:RETRY_DELAY" },
            { "-dd", "DEFAULT:TARGET_DIR_DEPTH" },
            { "--dir-depth", "DEFAULT:TARGET_DIR_DEPTH" },
            { "-lf", "DEFAULT:LOCK_FILE" },
            { "--lock-file", "DEFAULT:LOCK_FILE" },
            { "-ll", "DEFAULT:LOG_LEVEL" },
            { "--log-level", "DEFAULT:LOG_LEVEL" },
            { "-i", "DEFAULT:IGNORE" },
            { "--ignore", "DEFAULT:IGNORE" },
            { "-if", "DEFAULT:IGNORE_FILE" },
            { "--ignore-file", "DEFAULT:IGNORE_FILE" }
        };
    }

    public string? Get(string section, string key, string? defaultValue = null)
    {
        // Bei INI-Dateien werden Sektionen verwendet, bei JSON könnte die Hierarchie durch ":" getrennt sein
        var configKey = section + ":" + key;
        var value = Configuration[configKey];
            
        if (value != null)
        {
            Log.Debug("Konfigurationswert gefunden: {Section}:{Key}={Value}", section, key, value);
            return value;
        }
            
        if (defaultValue != null)
        {
            Log.Debug("Konfigurationswert nicht gefunden: {Section}:{Key}, verwende Standardwert: {DefaultValue}", section, key, defaultValue);
            // Wir verfolgen auch Standardwerte in unserem Dictionary
            ConfigSources[configKey] = SourceDefault;
        }
        else
            Log.Warning("Konfigurationswert nicht gefunden: {Section}:{Key}, kein Standardwert", section, key);
            
        return defaultValue;
    }
    
    /// <summary>
    /// Gibt die Quelle eines Konfigurationswerts zurück
    /// </summary>
    private string GetConfigSource(string section, string key)
    {
        var configKey = section + ":" + key;
        return ConfigSources.GetValueOrDefault(configKey, SourceDefault);
    }
    
    /// <summary>
    /// Generiert eine Dokumentation der aktuellen Konfiguration mit Angabe der Quellen
    /// </summary>
    /// <param name="configPath">Der Pfad zur Konfigurationsdatei</param>
    /// <returns>Eine Liste mit Konfigurationseinträgen für die Dokumentation</returns>
    public List<string> GenerateConfigDoku(string configPath)
    {
        var result = new List<string>
        {
            // Konfigurationsquellen dokumentieren
            $"Konfigurationsdatei: {configPath}",
            ""
        };

        // Konfigurationswerte nach Sektionen gruppiert dokumentieren
        foreach (var section in GetAllSections())
        {
            result.Add($"[{section}]");
            foreach (var item in Configuration.GetSection(section).AsEnumerable().Where(x => x.Value != null))
            {
                var key = item.Key.Replace(section + ":", "");
                if (string.IsNullOrEmpty(key)) continue;
                
                var value = item.Value!;
                
                // Sensible Werte maskieren (z.B. API-Keys, Tokens, Passwörter)
                // Wert durch Platzhalter ersetzen
                value = IsSensitiveKey(key) ? "***SECRET***" :
                    // Maskiere trotzdem sensible Daten, die vielleicht im Wert enthalten sind
                    SensitiveDataManager.MaskSensitiveData(value);
                
                var source = GetConfigSource(section, key);
                result.Add($"{key}={value} ({source})");
            }
            result.Add("");
        }
        
        return result;
    }
    
    /// <summary>
    /// Prüft, ob ein Konfigurationsschlüssel sensible Daten enthält und maskiert werden sollte
    /// </summary>
    private static bool IsSensitiveKey(string key)
    {
        // Liste von Schlüsselnamen, die als sensibel eingestuft werden
        var sensitiveKeyPatterns = new[]
        {
            "key",
            "secret",
            "password",
            "token",
            "credential",
            "apikey"
        };
        
        return sensitiveKeyPatterns.Any(pattern => 
            key.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
    
    private List<string> GetAllSections()
    {
        // Alle Kind-Sektionen der Root-Ebene ermitteln

        return Configuration.GetChildren().Select(section => section.Key).ToList();
    }
    
    /// <summary>
    /// Verarbeitet Kommandozeilenargumente vor, um Parameter ohne Wert zu unterstützen
    /// </summary>
    /// <param name="args">Original-Kommandozeilenargumente</param>
    /// <returns>Verarbeitete Kommandozeilenargumente</returns>
    private static string[] PreprocessCommandLineArgs(string[] args)
    {
        var result = new List<string>();
        var flagParameters = new[] { "--safe-mode", "-sm", "--dry-run", "-d" };
        
        for (var i = 0; i < args.Length; i++)
        {
            // Prüfen, ob es sich um einen Flag-Parameter handelt
            if (flagParameters.Contains(args[i]))
            {
                // Wenn das nächste Argument ein neuer Parameter ist oder wir am Ende sind,
                // fügen wir den Flag-Parameter mit dem Wert "true" hinzu
                if (i == args.Length - 1 || args[i+1].StartsWith('-'))
                {
                    result.Add(args[i]);
                    result.Add("true");
                    continue; // Wir haben den Parameter bereits hinzugefügt
                }
            }
            
            // Normaler Parameter, unverändert hinzufügen
            result.Add(args[i]);
        }
        
        return result.ToArray();
    }
}