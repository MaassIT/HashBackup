using System.Text.Json.Serialization;
using IniParser;

namespace HashBackup
{
    public class ConfigLoader
    {
        private Dictionary<string, Dictionary<string, string>> Config { get; set; } = new();

        public ConfigLoader(string configPath)
        {
            Log.Debug("Lade Konfiguration aus Datei: {ConfigPath}", configPath);
            
            if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                LoadJson(configPath);
            else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                LoadIni(configPath);
            else
                throw new ArgumentException("Nur .ini oder .json werden unterstützt.");
                
            Log.Information("Konfiguration erfolgreich geladen mit {SectionCount} Sektionen", Config.Count);
        }

        private void LoadIni(string path)
        {
            Log.Debug("Lade INI-Konfiguration: {Path}", path);
            var parser = new FileIniDataParser();
            var data = parser.ReadFile(path);
            foreach (var section in data.Sections)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in section.Keys)
                    dict[key.KeyName] = key.Value;
                Config[section.SectionName] = dict;
                Log.Debug("INI-Sektion geladen: {SectionName} mit {KeyCount} Schlüsseln", section.SectionName, section.Keys.Count);
            }
        }

        private void LoadJson(string path)
        {
            Log.Debug("Lade JSON-Konfiguration: {Path}", path);
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                json, ConfigJsonContext.Default.DictionaryStringDictionaryStringString);
            if (dict != null)
            {
                Config = dict;
                Log.Debug("JSON-Konfiguration geladen mit {SectionCount} Sektionen", Config.Count);
            }
            else
            {
                Log.Warning("JSON-Konfiguration konnte nicht deserialisiert werden: {Path}", path);
            }
        }

        public string? Get(string section, string key, string? defaultValue = null)
        {
            if (Config.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var val))
            {
                Log.Debug("Konfigurationswert gefunden: [{Section}]:{Key}={Value}", section, key, val);
                return val;
            }
            
            if (defaultValue != null)
                Log.Debug("Konfigurationswert nicht gefunden: [{Section}]:{Key}, verwende Standardwert: {DefaultValue}", section, key, defaultValue);
            else
                Log.Debug("Konfigurationswert nicht gefunden: [{Section}]:{Key}, kein Standardwert", section, key);
                
            return defaultValue;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
