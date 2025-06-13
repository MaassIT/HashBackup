using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace HashBackup;

public class ConfigLoader
{
    private IConfiguration Configuration { get; set; }

    public ConfigLoader(string configPath, string[] args)
    {
        Log.Debug("Lade Konfiguration aus Datei: {ConfigPath}", configPath);
            
        // Konfigurationsbuilder erstellen
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException())
            .SetFileLoadExceptionHandler(context => 
            {
                Log.Error(context.Exception, "Fehler beim Laden der Konfigurationsdatei {Path}", 
                    context.Provider.Source.Path);
            });
            
        // Je nach Dateierweiterung den passenden Provider verwenden
        if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("JSON-Konfigurationsprovider konfiguriert für {Path}", configPath);
        }
        else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddIniFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("INI-Konfigurationsprovider konfiguriert für {Path}", configPath);
        }
        else
        {
            throw new ArgumentException("Nur .ini oder .json werden unterstützt.");
        }
        
        // Umgebungsvariablen mit Prefix hinzufügen (höhere Priorität als Konfigurationsdatei)
        builder.AddEnvironmentVariables("HASHBACKUP_");
        Log.Debug("Umgebungsvariablen mit Prefix 'HASHBACKUP_' zur Konfiguration hinzugefügt");
        
        // Kommandozeilenargumente hinzufügen (höchste Priorität)
        if (args != null && args.Length > 0)
        {
            builder.AddCommandLine(args, GetCommandLineMapping());
            Log.Debug("Kommandozeilenargumente zur Konfiguration hinzugefügt");
        }
            
        // Konfiguration bauen
        Configuration = builder.Build();
        
        Log.Information("Konfiguration erfolgreich geladen");
    }

    // Dictionary für Mapping von Kurzoptionen zu Konfigurationsschlüsseln
    private Dictionary<string, string> GetCommandLineMapping()
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
            { "--dir-depth", "DEFAULT:TARGET_DIR_DEPTH" }
        };
    }

    public string? Get(string section, string key, string? defaultValue = null)
    {
        // Bei INI-Dateien werden Sektionen verwendet, bei JSON könnte die Hierarchie durch : getrennt sein
        var configKey = section + ":" + key;
        var value = Configuration[configKey];
            
        if (value != null)
        {
            Log.Debug("Konfigurationswert gefunden: {Section}:{Key}={Value}", section, key, value);
            return value;
        }
            
        if (defaultValue != null)
            Log.Debug("Konfigurationswert nicht gefunden: {Section}:{Key}, verwende Standardwert: {DefaultValue}", section, key, defaultValue);
        else
            Log.Warning("Konfigurationswert nicht gefunden: {Section}:{Key}, kein Standardwert", section, key);
                
        return defaultValue;
    }
}