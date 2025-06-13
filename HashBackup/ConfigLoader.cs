using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace HashBackup;

public class ConfigLoader
{
    private IConfiguration Configuration { get; set; }
    
    // Dictionary zur Verfolgung der Konfigurationsquellen
    private Dictionary<string, string> ConfigSources { get; set; }
    
    private const string SOURCE_FILE = "Konfigurationsdatei";
    private const string SOURCE_ENV = "Umgebungsvariable";
    private const string SOURCE_CMDLINE = "Kommandozeile";
    private const string SOURCE_DEFAULT = "Standardwert";

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
        IConfiguration fileConfig = null;
            
        // Je nach Dateierweiterung den passenden Provider verwenden
        if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("JSON-Konfigurationsprovider konfiguriert für {Path}", configPath);
            
            // Separate Konfiguration nur für die Datei
            var fileBuilder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath))
                .AddJsonFile(Path.GetFileName(configPath), optional: false, reloadOnChange: false);
            fileConfig = fileBuilder.Build();
        }
        else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddIniFile(Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("INI-Konfigurationsprovider konfiguriert für {Path}", configPath);
            
            // Separate Konfiguration nur für die Datei
            var fileBuilder = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(configPath))
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
                ConfigSources[item.Key] = SOURCE_FILE;
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
            ConfigSources[item.Key] = SOURCE_ENV;
        }
        
        // Kommandozeilenargumente hinzufügen (höchste Priorität)
        if (args != null && args.Length > 0)
        {
            builder.AddCommandLine(args, GetCommandLineMapping());
            Log.Debug("Kommandozeilenargumente zur Konfiguration hinzugefügt");
            
            // Separate Konfiguration für Kommandozeilenargumente
            var cmdBuilder = new ConfigurationBuilder().AddCommandLine(args, GetCommandLineMapping());
            var cmdConfig = cmdBuilder.Build();
            
            // Speichere Kommandozeilenargumente im ConfigSources-Dictionary
            foreach (var item in cmdConfig.AsEnumerable().Where(x => x.Value != null))
            {
                ConfigSources[item.Key] = SOURCE_CMDLINE;
            }
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
        {
            Log.Debug("Konfigurationswert nicht gefunden: {Section}:{Key}, verwende Standardwert: {DefaultValue}", section, key, defaultValue);
            // Wir verfolgen auch Standardwerte in unserem Dictionary
            ConfigSources[configKey] = SOURCE_DEFAULT;
        }
        else
            Log.Warning("Konfigurationswert nicht gefunden: {Section}:{Key}, kein Standardwert", section, key);
            
        return defaultValue;
    }
    
    /// <summary>
    /// Gibt die Quelle eines Konfigurationswerts zurück
    /// </summary>
    public string GetConfigSource(string section, string key)
    {
        var configKey = section + ":" + key;
        return ConfigSources.ContainsKey(configKey) ? ConfigSources[configKey] : SOURCE_DEFAULT;
    }
    
    /// <summary>
    /// Generiert eine Dokumentation der aktuellen Konfiguration mit Angabe der Quellen
    /// </summary>
    /// <param name="configPath">Der Pfad zur Konfigurationsdatei, der in der Dokumentation erwähnt wird</param>
    /// <returns>Eine Liste mit Konfigurationseinträgen für die Dokumentation</returns>
    public List<string> GenerateConfigDoku(string configPath)
    {
        var result = new List<string>();
        
        // Konfigurationsquellen dokumentieren
        result.Add($"Konfigurationsdatei: {configPath}");
        result.Add($"Backup ausgeführt am: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        result.Add("");
        
        // Konfigurationswerte nach Sektionen gruppiert dokumentieren
        foreach (var section in GetAllSections())
        {
            result.Add($"[{section}]");
            foreach (var item in Configuration.GetSection(section).AsEnumerable().Where(x => x.Value != null))
            {
                var key = item.Key.Replace(section + ":", "");
                if (!string.IsNullOrEmpty(key))
                {
                    var source = GetConfigSource(section, key);
                    result.Add($"{key}={item.Value} ({source})");
                }
            }
            result.Add("");
        }
        
        return result;
    }
    
    private IEnumerable<string> GetAllSections()
    {
        var result = new List<string>();
        
        // Alle Kind-Sektionen der Root-Ebene ermitteln
        foreach (var section in Configuration.GetChildren())
        {
            result.Add(section.Key);
        }
        
        return result;
    }
}