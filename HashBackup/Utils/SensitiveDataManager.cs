namespace HashBackup.Utils;

/// <summary>
/// Manager für sensible Daten und Secrets, die in Logs und anderen Ausgaben maskiert werden sollen
/// </summary>
public static class SensitiveDataManager
{
    private static readonly HashSet<string> Secrets = new(StringComparer.OrdinalIgnoreCase);
    
    // Standard-Ersetzungstext für sensible Werte
    private const string SecretPlaceholder = "***SECRET***";
    
    /// <summary>
    /// Fügt einen geheimen Schlüssel (z.B. API Key, Token, Passwort) hinzu, der in Logs und Ausgaben maskiert werden soll
    /// </summary>
    /// <param name="secret">Der zu maskierende geheime Schlüssel</param>
    public static void RegisterSecret(string secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
        {
            Secrets.Add(secret);
        }
    }
    
    /// <summary>
    /// Entfernt einen geheimen Schlüssel aus der Maskierungsliste
    /// </summary>
    /// <param name="secret">Der zu entfernende geheime Schlüssel</param>
    public static void UnregisterSecret(string secret)
    {
        Secrets.Remove(secret);
    }
    
    /// <summary>
    /// Prüft, ob ein Text geheime Daten enthält, und maskiert diese
    /// </summary>
    /// <param name="text">Der zu prüfende Text</param>
    /// <returns>Text mit maskierten geheimen Daten</returns>
    public static string MaskSensitiveData(string text)
    {
        if (string.IsNullOrEmpty(text) || Secrets.Count == 0)
        {
            return text;
        }
        
        var result = text;
        foreach (var secret in Secrets)
        {
            if (!string.IsNullOrEmpty(secret) && result.Contains(secret))
            {
                // Ersetze den geheimen Wert vollständig durch den Platzhalter
                result = result.Replace(secret, SecretPlaceholder);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Gibt die Anzahl der registrierten geheimen Schlüssel zurück
    /// </summary>
    public static int GetSecretCount()
    {
        return Secrets.Count;
    }
}
