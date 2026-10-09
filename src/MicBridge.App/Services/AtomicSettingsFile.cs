using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MicBridge.App.Services;

internal static class AtomicSettingsFile
{
    internal static string ReadAllText(string path)
    {
        using var lease = new FileLease(path);
        return ReadCore(path);
    }

    internal static string? ReadIfExists(string path)
    {
        using var lease = new FileLease(path);
        return File.Exists(path) ? ReadCore(path) : null;
    }

    private static string ReadCore(string path)
    {
        // Readers keep their own complete version while another instance replaces the file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void WriteAllText(string path, string text)
    {
        path = Path.GetFullPath(path);
        using var lease = new FileLease(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else
            {
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path))
                {
                    // Another instance completed its first save after our existence check.
                    File.Replace(temporary, path, null);
                }
            }
        }
        finally
        {
            // This invocation owns only its unique staging file.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static bool IsTemporaryName(string name, string destinationName)
    {
        string prefix = destinationName + ".";
        return name.Length == prefix.Length + 32 + 4 && name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.EndsWith(".tmp", StringComparison.Ordinal) &&
            Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
    }

    private sealed class FileLease : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _owned;
        internal FileLease(string path)
        {
            using var hash = SHA256.Create();
            string identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
            _mutex = new Mutex(false, "Local\\MicBridge.Settings." + identity);
            try
            {
                try { _owned = _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { _owned = true; }
                if (!_owned) throw new IOException("其他音桥实例正在保存设置，请稍后重试。");
            }
            catch { _mutex.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (_owned) { _owned = false; _mutex.ReleaseMutex(); } }
            finally { _mutex.Dispose(); }
        }
    }
}
