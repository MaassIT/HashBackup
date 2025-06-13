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
                RedirectStandardError = true,
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
#endif
    
}