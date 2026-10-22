using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher;

internal static partial class NodeRuntimeInstaller
{
    public static string? FindArchivePath(
        string runtimeDirectory,
        Architecture architecture)
    {
        if (!Directory.Exists(runtimeDirectory))
        {
            return null;
        }

        string[] archives = Directory.GetFiles(
            runtimeDirectory,
            $"node-v*-win-{GetArchitectureName(architecture)}.zip");
        return archives.Length switch
        {
            0 => null,
            1 => archives[0],
            _ => throw new InvalidDataException(
                "The package contains multiple Node.js runtime archives.")
        };
    }

    public static Version GetArchiveVersion(
        string archivePath,
        Architecture architecture)
    {
        Match match = ArchiveNameRegex().Match(Path.GetFileName(archivePath));
        if (!match.Success ||
            match.Groups["architecture"].Value != GetArchitectureName(architecture))
        {
            throw new InvalidDataException(
                $"Unexpected Node.js runtime archive: {Path.GetFileName(archivePath)}");
        }

        return NodeRuntimeResolver.ParseVersion(match.Groups["version"].Value);
    }

    public static string GetInstallDirectory(string archivePath) =>
        Path.Combine(
            HostDataPaths.GetProductLocalStateRoot(),
            "NodeJS",
            Path.GetFileNameWithoutExtension(archivePath));

    public static NodeRuntime EnsureInstalled(
        string archivePath,
        Action<string> log)
    {
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        Version version = GetArchiveVersion(archivePath, architecture);
        return EnsureInstalled(
            archivePath,
            GetInstallDirectory(archivePath),
            path => NodeRuntimeResolver.ResolvePath(
                path,
                version),
            log);
    }

    internal static NodeRuntime EnsureInstalled(
        string archivePath,
        string installDirectory,
        Func<string, NodeRuntime> resolveNode,
        Action<string> log)
    {
        string executablePath = Path.Combine(installDirectory, "node.exe");
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The packaged Node.js runtime archive was not found.",
                archivePath);
        }

        // LocalState is shared across Windows sessions. Keep validation and
        // publication on this thread because mutex ownership is thread-affine.
        using var mutex = new Mutex(
            initiallyOwned: false,
            GetInstallMutexName(installDirectory));
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            string stagingDirectory = $"{installDirectory}.extract";
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            if (File.Exists(executablePath))
            {
                try
                {
                    return resolveNode(executablePath);
                }
                catch (InvalidOperationException exception)
                {
                    log($"Reinstalling invalid bundled Node.js: {DiagnosticFailure.Describe(exception)}");
                }
            }

            try
            {
                ExtractRuntime(
                    archivePath,
                    stagingDirectory);
                NodeRuntime runtime = resolveNode(
                    Path.Combine(stagingDirectory, "node.exe"));

                if (Directory.Exists(installDirectory))
                {
                    Directory.Delete(installDirectory, recursive: true);
                }
                Directory.Move(stagingDirectory, installDirectory);
                return runtime with { ExecutablePath = executablePath };
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    internal static string GetInstallMutexName(string installDirectory)
    {
        string identity = Path.GetFullPath(installDirectory).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"Global\\OpenClawGatewayMSIX.NodeRuntime.{hash}";
    }

    private static void ExtractRuntime(
        string archivePath,
        string stagingDirectory)
    {
        string archiveRoot = Path.GetFileNameWithoutExtension(archivePath);
        string archivePrefix = archiveRoot + "/";
        string fullStagingDirectory =
            Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(stagingDirectory);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string archivePathName = entry.FullName.Replace('\\', '/');
            if (string.Equals(
                archivePathName,
                archiveRoot + "/",
                StringComparison.Ordinal))
            {
                continue;
            }

            if (!archivePathName.StartsWith(
                archivePrefix,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unexpected Node.js archive entry: {entry.FullName}");
            }

            bool isDirectory = string.IsNullOrEmpty(entry.Name);
            string relativePath = archivePathName[archivePrefix.Length..];
            if (isDirectory)
            {
                relativePath = relativePath.TrimEnd('/');
            }
            string[] segments = relativePath.Split('/');
            if (
                string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Contains(':', StringComparison.Ordinal) ||
                segments.Contains(string.Empty, StringComparer.Ordinal) ||
                segments.Contains(".", StringComparer.Ordinal) ||
                segments.Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Unsafe Node.js archive entry: {entry.FullName}");
            }

            string destinationPath = Path.GetFullPath(
                Path.Combine(stagingDirectory, relativePath));
            if (!destinationPath.StartsWith(
                fullStagingDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Unsafe Node.js archive entry: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidDataException(
                    $"Invalid Node.js archive entry: {entry.FullName}");
            }

            Directory.CreateDirectory(destinationDirectory);
            entry.ExtractToFile(destinationPath);
        }
    }

    [GeneratedRegex(
        @"^node-v(?<version>\d+\.\d+\.\d+)-win-(?<architecture>x64|arm64)\.zip$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveNameRegex();

    private static string GetArchitectureName(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Node.js runtime packaging does not support {architecture}.")
        };
}
