using System.Runtime.InteropServices;

namespace HashBackup.Utils;

public static class FileAttributesUtil
{
    // xattr für macOS/Linux, ADS für Windows
    public static void SetAttribute(string filePath, string attrName, string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // NTFS ADS: z.B. file.txt:attrName
            var adsPath = filePath + ":" + attrName;
            File.WriteAllText(adsPath, value);
        }
        else
        {
            // xattr (benötigt ggf. libattr oder Mono.Posix)
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                setxattr(filePath, attrName, bytes, bytes.Length, 0, 0);
            }
            catch (Exception ex)
            {
                throw new IOException($"Fehler beim Setzen von xattr: {ex.Message}");
            }
        }
    }

    public static string? GetAttribute(string filePath, string attrName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attrName;
            return File.Exists(adsPath) ? File.ReadAllText(adsPath) : null;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xattr",
                Arguments = $"-p \"{attrName}\" \"{filePath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc!.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0)
                return output.TrimEnd('\r', '\n');
        }
        catch (Exception ex)
        {
            Log.Debug("xattr-GetAttribute fehlgeschlagen: {Message}", ex.Message);
        }
        return null;
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
        byte[] value,
        ulong size
    );

    // Korrigierte Signatur für listxattr (nutze long und ulong für Kompatibilität)
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern long listxattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr namebuf,
        ulong size
    );
#endif

    // Listet alle xattr einer Datei auf (nur macOS/Linux)
    public static List<string> ListAttributes(string filePath)
    {
#if WINDOWS
            throw new PlatformNotSupportedException("xattr nur auf macOS/Linux verfügbar");
#else
        var list = new List<string>();
        var size = listxattr(filePath, IntPtr.Zero, 0);
        if (size <= 0)
        {
            var err = Marshal.GetLastWin32Error();
            Log.Debug("listxattr: size={Size} für {FilePath}, errno={Errno}", size, filePath, err);
            // Fallback: xattr-Kommandozeile verwenden
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xattr",
                    Arguments = $"\"{filePath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                var output = proc!.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var attributes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                list.AddRange(attributes);
                Log.Debug("xattr-Fallback: {Count} Attribute gefunden", attributes.Length);
            }
            catch (Exception ex)
            {
                Log.Debug("xattr-Fallback fehlgeschlagen: {Message}", ex.Message);
            }
            return list;
        }
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            var ret = listxattr(filePath, buf, (ulong)size);
            Log.Debug("listxattr: ret={RetVal} für {FilePath}", ret, filePath);
            if (ret > 0)
            {
                var start = 0;
                var managedBuf = new byte[(int)size];
                Marshal.Copy(buf, managedBuf, 0, (int)size);
                for (var i = 0; i < (int)size; i++)
                {
                    if (managedBuf[i] != 0) continue;
                    var attr = System.Text.Encoding.UTF8.GetString(managedBuf, start, i - start);
                    Log.Debug("xattr gefunden: '{Attr}'", attr);
                    list.Add(attr);
                    start = i + 1;
                }
            }
            else
            {
                Log.Debug("listxattr Rückgabewert <= 0: ret={RetVal}", ret);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
#endif
    }
}