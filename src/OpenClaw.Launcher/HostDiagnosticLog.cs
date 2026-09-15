using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Launcher;

internal sealed class HostDiagnosticLog : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Mutex _writeMutex;
    private bool _disposed;

    private HostDiagnosticLog(string path)
    {
        Path = path;
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The diagnostic log path has no directory.");
        }

        Directory.CreateDirectory(directory);
        string mutexSuffix = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        _writeMutex = new Mutex(
            initiallyOwned: false,
            $"Local\\OpenClawGatewayMSIX.Log.{mutexSuffix}");
    }

    public string Path { get; }

    public static HostDiagnosticLog Create()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        string? packageFamilyName = HostDataPaths.TryGetPackageFamilyName();
        string logRoot = packageFamilyName is null
            ? System.IO.Path.Combine(localAppData, "OpenClawGatewayMSIX")
            : System.IO.Path.Combine(
                localAppData,
                "Packages",
                packageFamilyName,
                "LocalState",
                "OpenClawGatewayMSIX");
        return Create(System.IO.Path.Combine(logRoot, "Logs", "openclaw.log"));
    }

    public static HostDiagnosticLog Create(string path) =>
        new(System.IO.Path.GetFullPath(path));

    public void Write(string message)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = _writeMutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    throw new IOException(
                        "Timed out waiting to append to the diagnostic log.");
                }

                // A fresh setup deliberately clears the installation state
                // root. Recreate the log directory before reopening the file.
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                using var stream = new FileStream(
                    Path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            finally
            {
                if (ownsMutex)
                {
                    _writeMutex.ReleaseMutex();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _writeMutex.Dispose();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

}
