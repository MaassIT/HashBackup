// filepath: /Users/christian/Projekte/MaassIT/HashBackup/HashBackup/Utils/IgnorePatternMatcher.cs
using System.Text.RegularExpressions;

namespace HashBackup.Utils;

/// <summary>
/// Stellt Funktionen zur Verfügung, um Datei- und Verzeichnispfade anhand von Mustern zu filtern
/// </summary>
public class IgnorePatternMatcher
{
    private readonly List<Regex> _regexPatterns = new();
    private readonly List<string> _exactMatches = new();
    
    /// <summary>
    /// Initialisiert eine neue Instanz der IgnorePatternMatcher-Klasse
    /// </summary>
    /// <param name="patterns">Die Liste der zu ignorierenden Muster</param>
    public IgnorePatternMatcher(IEnumerable<string> patterns)
    {
        if (patterns == null) return;
        
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
                
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                // Konvertiere Glob-Pattern in regex
                var regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                    
                _regexPatterns.Add(new Regex(regexPattern, RegexOptions.IgnoreCase));
            }
            else
            {
                // Exakte Übereinstimmungen können effizienter über direkte Vergleiche geprüft werden
                _exactMatches.Add(pattern);
            }
        }
        
        Log.Debug("Ignore-Pattern-Matcher initialisiert mit {ExactCount} exakten Mustern und {RegexCount} Regex-Mustern", 
            _exactMatches.Count, _regexPatterns.Count);
    }
    
    /// <summary>
    /// Prüft, ob ein Dateiname oder Pfad ignoriert werden soll
    /// </summary>
    /// <param name="path">Der zu prüfende Pfad</param>
    /// <param name="checkFullPath">Ob der vollständige Pfad oder nur der Dateiname geprüft werden soll</param>
    /// <returns>True, wenn der Pfad ignoriert werden soll, sonst False</returns>
    public bool ShouldIgnore(string path, bool checkFullPath = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
            
        var fileName = checkFullPath ? path : Path.GetFileName(path);
        
        // Prüfe zuerst exakte Übereinstimmungen (effizienter)
        if (_exactMatches.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            Log.Debug("Datei {Path} wird aufgrund eines exakten Musters ignoriert", path);
            return true;
        }
        
        // Prüfe dann Regex-Muster
        foreach (var regex in _regexPatterns)
        {
            if (!regex.IsMatch(fileName)) continue;
            Log.Debug("Datei {Path} wird aufgrund eines Regex-Musters ignoriert", path);
            return true;
        }
        
        return false;
    }
}
