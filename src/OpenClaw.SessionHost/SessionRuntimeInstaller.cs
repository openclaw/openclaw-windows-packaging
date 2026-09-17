using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--install-runtime</c> mode: installs the packaged Node.js runtime
/// into this account's own profile.
/// </summary>
/// <remarks>
/// <para>
/// The host extracts its own copy into the invoking user's package LocalState,
/// which this account cannot read. The archive is package content and is
/// readable by both, so the agent extracts its own rather than being handed
/// files another identity owns.
/// </para>
/// <para>
/// Running extraction here means every file is created by the account that
/// will run it.
/// </para>
/// </remarks>
internal static class SessionRuntimeInstaller
{
    private static readonly Guid LocalApplicationDataFolderId =
        new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        Func<string>? getLocalApplicationData = null,
        Func<string, bool>? tryPrependUserPath = null,
        Func<string, string?>? getRuntimeVersion = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;

        try
        {
            SessionRuntimeInstallRequest request =
                SessionRuntimeProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;

            string directory = Path.Combine(
                (getLocalApplicationData ??
                    GetLocalApplicationData)(),
                "OpenClawGatewayMSIX",
                "agent-node");
            string archiveRoot = Path.GetFileNameWithoutExtension(request.ArchivePath!);
            string executablePath = Path.Combine(
                directory,
                archiveRoot,
                "node.exe");
            string expectedVersion = GetArchiveVersion(request.ArchivePath!);
            if (!File.Exists(executablePath) ||
                !string.Equals(
                    (getRuntimeVersion ?? ReadRuntimeVersion)(executablePath),
                    expectedVersion,
                    StringComparison.Ordinal))
            {
                string stagingDirectory = Path.Combine(
                    directory,
                    $"{archiveRoot}.extract-{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(directory);
                    ZipFile.ExtractToDirectory(request.ArchivePath!, stagingDirectory, false);
                    string stagedRoot = Path.Combine(stagingDirectory, archiveRoot);
                    string stagedExecutable = Path.Combine(stagedRoot, "node.exe");
                    if (!File.Exists(stagedExecutable))
                    {
                        throw new SessionLaunchException(
                            "The installed Node.js runtime did not contain node.exe.");
                    }

                    string targetRoot = Path.Combine(directory, archiveRoot);
                    if (Directory.Exists(targetRoot))
                    {
                        Directory.Delete(targetRoot, recursive: true);
                    }
                    Directory.Move(stagedRoot, targetRoot);
                }
                finally
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, recursive: true);
                    }
                }
            }

            if (!File.Exists(executablePath))
            {
                throw new SessionLaunchException(
                    "The installed Node.js runtime did not contain node.exe.");
            }

            string runtimeDirectory = Path.GetDirectoryName(executablePath)
                ?? throw new SessionLaunchException(
                    "The installed Node.js runtime has no executable directory.");
            bool pathUpdated = request.UpdateUserPath &&
                (tryPrependUserPath ?? TryPrependUserPath)(runtimeDirectory);

            writeFile(
                resultPath,
                SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
                {
                    RequestId = requestId,
                    ExecutablePath = executablePath,
                    Version = expectedVersion,
                    ArchiveName = Path.GetFileName(request.ArchivePath!),
                    UserPathUpdated = pathUpdated
                }));
            return 0;
        }

        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or BadImageFormatException or
            System.ComponentModel.Win32Exception)
        {
            TryWriteFailure(writeFile, resultPath, requestId, exception.Message);
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    private static string GetArchiveVersion(string archivePath)
    {
        string archiveName = Path.GetFileNameWithoutExtension(archivePath);
        const string prefix = "node-v";
        const string platformMarker = "-win-";
        int platformIndex = archiveName.LastIndexOf(
            platformMarker,
            StringComparison.OrdinalIgnoreCase);
        if (!archiveName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            platformIndex <= prefix.Length)
        {
            throw new SessionLaunchException(
                $"The packaged Node.js runtime archive has an unexpected name: {archiveName}");
        }

        string version = archiveName[prefix.Length..platformIndex];
        return Version.TryParse(version, out Version? parsed)
            ? parsed.ToString()
            : throw new SessionLaunchException(
                $"The packaged Node.js runtime archive has an invalid version: {archiveName}");
    }

    private static string GetLocalApplicationData()
    {
        int result = SHGetKnownFolderPath(
            LocalApplicationDataFolderId,
            flags: 0,
            token: IntPtr.Zero,
            out IntPtr path);
        if (result < 0)
        {
            throw new SessionLaunchException(
                $"Windows could not resolve the local application data directory " +
                $"(HRESULT 0x{result:X8}).");
        }

        try
        {
            return Marshal.PtrToStringUni(path)
                ?? throw new SessionLaunchException(
                    "Windows returned an empty local application data directory.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    private static string? ReadRuntimeVersion(string executablePath)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" }
            });
            if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process?.Kill(entireProcessTree: true);
                return null;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0
                ? output.TrimStart('v')
                : null;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the runtime directory at the front of this account's persistent
    /// user <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prepended, not appended: a machine-wide Node.js installation is common,
    /// and the packaged runtime is the one this installation supports.
    /// </para>
    /// <para>
    /// This covers processes that build their environment from the registry.
    /// It is not what governs the processes this package starts - those inherit
    /// an environment - so it is deliberately paired with the launch-time
    /// prepend rather than relied on alone.
    /// </para>
    /// <para>
    /// Only this account's own hive is touched, never the machine's.
    /// </para>
    /// </remarks>
    internal static bool TryPrependUserPath(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using RegistryKey? environment = Registry.CurrentUser.OpenSubKey(
            "Environment", writable: true);
        if (environment is null)
        {
            return false;
        }

        // GetValue expands REG_EXPAND_SZ by default, which would bake the
        // current expansion of every %VAR% in the value back into the registry.
        object? raw = environment.GetValue(
            "Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string current = raw as string ?? string.Empty;
        RegistryValueKind kind = raw is null
            ? RegistryValueKind.ExpandString
            : environment.GetValueKind("Path");

        string updated = BuildPath(current, directory);
        if (string.Equals(updated, current, StringComparison.Ordinal))
        {
            return false;
        }

        environment.SetValue("Path", updated, kind);
        return true;
    }

    /// <summary>
    /// Builds the new value: the directory first, every unrelated entry after,
    /// and any previous runtime directory dropped.
    /// </summary>
    /// <remarks>
    /// A previous version's directory is removed rather than left in place, so
    /// repeated setups across upgrades cannot accumulate stale runtimes ahead
    /// of the current one.
    /// </remarks>
    internal static string BuildPath(string current, string directory)
    {
        string? previousRoot = Path.GetDirectoryName(directory);
        List<string> entries = [directory];

        foreach (string entry in current.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0 ||
                string.Equals(
                    trimmed.TrimEnd(Path.DirectorySeparatorChar),
                    directory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                IsPreviousRuntime(trimmed, previousRoot))
            {
                continue;
            }

            entries.Add(trimmed);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    private static bool IsPreviousRuntime(string entry, string? runtimeRoot)
    {
        if (string.IsNullOrEmpty(runtimeRoot))
        {
            return false;
        }

        string parent = Path.GetDirectoryName(
            entry.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        return string.Equals(
            parent.TrimEnd(Path.DirectorySeparatorChar),
            runtimeRoot.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TryWriteFailure(
        Action<string, string> writeFile,
        string resultPath,
        string? requestId,
        string message)
    {
        try
        {
            writeFile(
                resultPath,
                SessionRuntimeProtocol.SerializeResult(new SessionRuntimeInstallResult
                {
                    RequestId = requestId,
                    Error = message
                }));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
