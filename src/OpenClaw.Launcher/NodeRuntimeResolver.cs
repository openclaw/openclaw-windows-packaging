using System.Diagnostics;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher;

internal sealed record NodeRuntime(
    string ExecutablePath,
    Version Version,
    Architecture Architecture);

internal static partial class NodeRuntimeResolver
{
    public const string InstallCommand =
        "winget install --id OpenJS.NodeJS.LTS --exact --source winget";

    private static readonly TimeSpan VersionQueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly NodeVersionRange[] SupportedVersionRanges =
    [
        new(new Version(22, 22, 3), 23),
        new(new Version(24, 15, 0), 25),
        new(new Version(25, 9, 0), null)
    ];

    public static string SupportedVersions { get; } = string.Join(
        " || ",
        SupportedVersionRanges.Select(range =>
            range.ExclusiveMajor is int exclusiveMajor
                ? $">={range.Minimum} <{exclusiveMajor}"
                : $">={range.Minimum}"));

    public static Task<NodeRuntime> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveAsync(
            FindPathCandidates(),
            QueryVersionAsync,
            ReadArchitecture,
            RuntimeInformation.ProcessArchitecture,
            cancellationToken);

    internal static async Task<NodeRuntime> ResolveAsync(
        IReadOnlyList<string> candidates,
        Func<string, CancellationToken, Task<string>> queryVersion,
        Func<string, Architecture> readArchitecture,
        Architecture requiredArchitecture,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                CreateFailureMessage("Node.js was not found on PATH."));
        }

        var failures = new List<string>();
        foreach (string candidate in candidates)
        {
            try
            {
                string output = await queryVersion(candidate, cancellationToken)
                    .ConfigureAwait(false);
                Version version = ParseVersion(output);
                if (!IsSupported(version))
                {
                    throw new InvalidDataException(
                        $"version {version} is unsupported; required {SupportedVersions}");
                }

                Architecture architecture = readArchitecture(candidate);
                if (architecture != requiredArchitecture)
                {
                    throw new InvalidDataException(
                        $"architecture {architecture} does not match {requiredArchitecture}");
                }

                return new NodeRuntime(candidate, version, architecture);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                BadImageFormatException or
                InvalidDataException or
                InvalidOperationException or
                TimeoutException or
                Win32Exception)
            {
                failures.Add($"{candidate}: {exception.Message}");
            }
        }

        throw new InvalidOperationException(
            CreateFailureMessage(
                "No compatible Node.js runtime was found. " +
                string.Join(" ", failures)));
    }

    internal static bool IsSupported(Version version) =>
        SupportedVersionRanges.Any(range =>
            version >= range.Minimum &&
            (
                range.ExclusiveMajor is null ||
                version.Major < range.ExclusiveMajor
            ));

    internal static Version ParseVersion(string output)
    {
        Match match = NodeVersionRegex().Match(output.Trim());
        if (
            !match.Success ||
            !Version.TryParse(match.Groups["version"].Value, out Version? version))
        {
            throw new InvalidDataException(
                $"Node.js returned an invalid version: {output.Trim()}");
        }

        return version;
    }

    internal static string CreateFailureMessage(string detail) =>
        $"{detail}{Environment.NewLine}" +
        $"Install a supported Node.js runtime ({SupportedVersions}):{Environment.NewLine}" +
        $"  {InstallCommand}{Environment.NewLine}" +
        "Then open a new terminal and retry.";

    private static List<string> FindPathCandidates()
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return [];
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in pathValue.Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, "node.exe"));
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate) && seen.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static async Task<string> QueryVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--version");

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start Node.js.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(VersionQueryTimeout);

        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
                timeout.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(
                timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await standardOutput.ConfigureAwait(false);
            string error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Node.js version query exited with code {process.ExitCode}: " +
                    error.Trim());
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            throw new TimeoutException("Node.js version query timed out.");
        }
    }

    private static Architecture ReadArchitecture(string executablePath)
    {
        using FileStream stream = File.OpenRead(executablePath);
        using var peReader = new PEReader(stream);
        return peReader.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => Architecture.X64,
            Machine.Arm64 => Architecture.Arm64,
            Machine.I386 => Architecture.X86,
            Machine.Arm => Architecture.Arm,
            Machine machine => throw new InvalidDataException(
                $"Node.js has unsupported executable architecture 0x{(ushort)machine:X4}")
        };
    }

    [GeneratedRegex(
        @"^v?(?<version>\d+\.\d+\.\d+)(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NodeVersionRegex();

    private sealed record NodeVersionRange(
        Version Minimum,
        int? ExclusiveMajor);
}
