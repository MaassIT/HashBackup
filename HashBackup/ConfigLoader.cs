using System.Text.Json.Serialization;
using IniParser;

namespace HashBackup
{
    public class ConfigLoader
    {
        private Dictionary<string, Dictionary<string, string>> Config { get; set; } = new();

        public ConfigLoader(string configPath)
        {
            if (configPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                LoadJson(configPath);
            else if (configPath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                LoadIni(configPath);
            else
                throw new ArgumentException("Nur .ini oder .json werden unterstützt.");
        }

        private void LoadIni(string path)
        {
            var parser = new FileIniDataParser();
            var data = parser.ReadFile(path);
            foreach (var section in data.Sections)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in section.Keys)
                    dict[key.KeyName] = key.Value;
                Config[section.SectionName] = dict;
            }
        }

        private void LoadJson(string path)
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                json, ConfigJsonContext.Default.DictionaryStringDictionaryStringString);
            if (dict != null)
                Config = dict;
        }

        public string? Get(string section, string key, string? defaultValue = null)
        {
            if (Config.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var val))
                return val;
            return defaultValue;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
