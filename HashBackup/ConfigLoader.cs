using Microsoft.Extensions.Configuration;

namespace HashBackup;

public class ConfigLoader
{
    private IConfiguration Configuration { get; set; }

    public ConfigLoader(string configPath)
    {
        Log.Debug("Lade Konfiguration aus Datei: {ConfigPath}", configPath);
            
        // Konfigurationsbuilder erstellen
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(configPath) ?? throw new InvalidOperationException())
            .SetFileLoadExceptionHandler(context => 
            {
                Log.Error("Fehler beim Laden der Konfigurationsdatei {Path}: {Exception}", 
                    context.Provider.Source.Path, context.Exception);
            });
            
        // Je nach Dateierweiterung den passenden Provider verwenden
        if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddJsonFile(System.IO.Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("JSON-Konfigurationsprovider konfiguriert für {Path}", configPath);
        }
        else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddIniFile(System.IO.Path.GetFileName(configPath), optional: false, reloadOnChange: true);
            Log.Debug("INI-Konfigurationsprovider konfiguriert für {Path}", configPath);
        }
        else
        {
            throw new ArgumentException("Nur .ini oder .json werden unterstützt.");
        }
            
        // Konfiguration bauen
        Configuration = builder.Build();
            
        Log.Information("Konfiguration erfolgreich geladen");
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