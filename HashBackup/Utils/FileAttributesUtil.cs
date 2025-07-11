using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Globalization;

namespace HashBackup.Utils;

[SuppressMessage("Interoperability", "SYSLIB1054:Verwenden Sie \\\"LibraryImportAttribute\\\" anstelle von \\\"DllImportAttribute\\\", um P/Invoke-Marshallingcode zur Kompilierzeit zu generieren.")]
public static class FileAttributesUtil
{
    // Cache für Attribute, um wiederholte Zugriffe zu vermeiden
    private static readonly ConcurrentDictionary<string, string> AttributeCache = new();
    
    // xattr für macOS/Linux, ADS für Windows
    public static void SetAttribute(string filePath, string attrName, string value)
    {
        // Aktualisiere den Cache
        var cacheKey = $"{filePath}:{attrName}";
        AttributeCache[cacheKey] = value;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // NTFS ADS: z.B. file.txt:attrName
            var adsPath = filePath + ":" + attrName;
            File.WriteAllText(adsPath, value);
        }
        else
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                setxattr(filePath, attrName, bytes, bytes.Length, 0, 0);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Fehler beim Setzen des Attributs {AttrName} für {FilePath}", attrName, filePath);
            }
        }
    }

    // Konvertiert DateTime zu Unix-Timestamp (wie in Python's os.path.getmtime)
    public static string DateTimeToUnixTimestamp(DateTime dateTime)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var unixTimestamp = (dateTime - epoch).TotalSeconds;
        return unixTimestamp.ToString(CultureInfo.InvariantCulture);
    }

    // Konvertiert Unix-Timestamp zu DateTime
    public static DateTime UnixTimestampToDateTime(string timestamp)
    {
        if (!double.TryParse(timestamp, NumberStyles.Any, CultureInfo.InvariantCulture, out var unixTimestamp))
            return DateTime.MinValue;
        
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return epoch.AddSeconds(unixTimestamp);

    }

    public static string? GetAttribute(string filePath, string attrName)
    {
        // Versuche zuerst aus dem Cache zu lesen
        var cacheKey = $"{filePath}:{attrName}";
        if (AttributeCache.TryGetValue(cacheKey, out var cachedValue))
        {
            return cachedValue;
        }
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attrName;
            var result = File.Exists(adsPath) ? File.ReadAllText(adsPath) : null;
            
            // Cache das Ergebnis
            if (result != null)
                AttributeCache[cacheKey] = result;
            
            return result;
        }

        try
        {
            // Verwende den nativen getxattr Aufruf anstelle eines Prozess-Starts
#if !WINDOWS
            // Bestimme zunächst die Größe des Attributs
            
            // Lese das Attribut
            var buffer = new byte[100];
            var readSize = getxattr(filePath, attrName, buffer, (ulong)buffer.Length);
            if (readSize <= 0) return null;
            
            var result = System.Text.Encoding.UTF8.GetString(buffer[..(int)readSize]);
            
            // Cache das Ergebnis
            AttributeCache[cacheKey] = result;
            
            return result;
#else
            // Fallback für Windows
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xattr",
                Arguments = $"-p \"{attrName}\" \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc!.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            
            if (proc.ExitCode == 0)
            {
                var result = output.TrimEnd('\r', '\n');
                // Cache das Ergebnis
                AttributeCache[cacheKey] = result;
                return result;
            }
#endif
        }
        catch (Exception ex)
        {
            Log.Debug("xattr-GetAttribute fehlgeschlagen: {Message}", ex.Message);
        }
        return null;
    }
    
    /// <summary>
    /// Lädt Attribute für mehrere Dateien in einer Batch-Operation vor
    /// </summary>
    /// <param name="filePaths">Liste der Dateipfade</param>
    /// <param name="attrNames">Liste der Attributnamen, die vorgeladen werden sollen</param>
    public static void PreloadAttributes(IEnumerable<string> filePaths, IEnumerable<string> attrNames)
    {
        // Unter Windows ist eine Batch-Operation nicht sinnvoll, da jeder Zugriff einen Dateizugriff bedeutet
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            var filePathsList = filePaths.ToList();
            var attrNamesList = attrNames.ToList();
            
            Log.Debug("Lade Attribute für {FileCount} Dateien und {AttrCount} Attributnamen vor", 
                filePathsList.Count, attrNamesList.Count);
            
            // Berechne optimale Batchgröße basierend auf der Anzahl der Dateien und Attribute
            var optimalBatchSize = CalculateOptimalBatchSize(filePathsList.Count, attrNamesList.Count);
            
            // Beschränke die Parallelität auf eine sinnvolle Anzahl
            var processorCount = Environment.ProcessorCount;
            var parallelOptions = new ParallelOptions { 
                MaxDegreeOfParallelism = Math.Max(1, processorCount > 4 ? processorCount - 2 : processorCount / 2) 
            };

            Log.Debug("Verwende Batchgröße {BatchSize} mit maximal {Threads} parallelen Threads",
                optimalBatchSize, parallelOptions.MaxDegreeOfParallelism);

            // Verarbeite in Batches, um Überlastung zu vermeiden
            for (var i = 0; i < filePathsList.Count; i += optimalBatchSize)
            {
                var batch = filePathsList.Skip(i).Take(optimalBatchSize).ToList();
    
                Parallel.ForEach(batch, parallelOptions, (filePath, _) =>
                {
                    try 
                    {
                        foreach (var attrName in attrNamesList)
                        {
                            GetAttribute(filePath, attrName);
                        }
                    }
                    catch (Exception ex) 
                    {
                        // Fehler bei einzelnen Dateien protokollieren, aber weitermachen
                        Log.Warning("Fehler beim Laden der Attribute für {FilePath}: {Error}", 
                            filePath, ex.Message);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fehler beim Vorladen von Attributen");
        }
    }

    /// <summary>
    /// Löscht alle Einträge aus dem Attribut-Cache
    /// </summary>
    public static void ClearCache()
    {
        AttributeCache.Clear();
    }

#if !WINDOWS
    // Linux/macOS xattr via P/Invoke
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int setxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, int size, int position, int options);

    // Korrigierte Signatur für getxattr (nutze long und ulong für Kompatibilität)
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern long getxattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[]? value,
        ulong size,
        uint position = 0,
        int options = 0
    );
#endif
    
    // Neue Methode zur Berechnung der optimalen Batchgröße
    private static int CalculateOptimalBatchSize(int fileCount, int attrCount)
    {
        // Basis-Batchgröße
        const int baseBatchSize = 1000;
        
        // Reduziere Batchgröße bei vielen Attributen, erhöhe bei wenigen
        var attributeFactor = Math.Max(1.0, 5.0 / Math.Max(1, attrCount));
        
        // Berücksichtige die Gesamtanzahl der Dateien
        var sizeFactor = Math.Min(1.0, (double)fileCount / 10000);
        
        var result = (int)(baseBatchSize * attributeFactor * (0.5 + sizeFactor));
        
        // Stelle sicher, dass die Batchgröße sinnvoll begrenzt ist
        return Math.Max(100, Math.Min(result, 5000));
    }
}