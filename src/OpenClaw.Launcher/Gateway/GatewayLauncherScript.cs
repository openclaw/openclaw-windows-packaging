using System.Text;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Generates the launcher files both persistence lanes invoke.
/// </summary>
internal static class GatewayLauncherScript
{
    /// <summary>
    /// Identifies a file this installation generated, so a file that happens to
    /// share the name is reported rather than silently overwritten.
    /// </summary>
    public const string Marker = "@rem openclaw-gateway-launcher v1";

    private const string ScriptHostMarker = "// openclaw-gateway-launcher v1";

    private const string PowerShellMarker = "# openclaw-gateway-launcher v1";

    public const string ControlApplicationId = "Control";

    public const string ControlArguments = "gateway-service start --recovery";

    private const string LegacyControlArguments = "gateway-service start";

    public static string Create(string workingDirectory, string activationScriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationScriptPath);

        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            $"& '{QuotePowerShell(activationScriptPath)}'"));

        // WScript has no console. Run hidden and wait so both persistence lanes
        // retain the recovery command's exit status rather than reporting spawn success.
        return string.Join(
            "\r\n",
            ScriptHostMarker,
            "var shell = new ActiveXObject(\"WScript.Shell\");",
            $"shell.CurrentDirectory = \"{QuoteJavaScript(workingDirectory)}\";",
            "var command = '\"' + shell.ExpandEnvironmentStrings(\"%SystemRoot%\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\") + '\"';",
            $"command += \" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}\";",
            "WScript.Quit(shell.Run(command, 0, true));",
            string.Empty);
    }

    public static string CreateActivationScript(string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);

        // Resolve the alias from this package's Control application. In particular,
        // a patched identity's alias must never fall back to the base installation.
        return string.Join(
            "\r\n",
            PowerShellMarker,
            "$ErrorActionPreference = 'Stop'",
            $"$packageFamilyName = '{QuotePowerShell(packageFamilyName)}'",
            $"$package = Get-AppxPackage -Name '{QuotePowerShell(GetPackageName(packageFamilyName))}' | Where-Object {{ $_.PackageFamilyName -eq $packageFamilyName }} | Select-Object -First 1",
            "if ($null -eq $package) { throw \"Package '$packageFamilyName' is not registered for the current user.\" }",
            "if ($package.Status -inotmatch '^(Ok|Ready)$') { throw \"Package '$packageFamilyName' is not ready (status $($package.Status)).\" }",
            "$manifestPath = Join-Path $package.InstallLocation 'AppxManifest.xml'",
            "[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw",
            $"$applicationId = '{ControlApplicationId}'",
            "$application = @($manifest.Package.Applications.Application | Where-Object { $_.Id -eq $applicationId }) | Select-Object -First 1",
            "if ($null -eq $application) { throw \"Package '$packageFamilyName' does not declare application '$applicationId'.\" }",
            "if ([string]$application.Executable -ne 'openclaw.exe') { throw \"Package '$packageFamilyName' application '$applicationId' does not target openclaw.exe.\" }",
            "$aliases = @($application.Extensions.Extension | Where-Object { $_.Category -eq 'windows.appExecutionAlias' } | ForEach-Object { $_.AppExecutionAlias.ExecutionAlias })",
            "if ($aliases.Count -ne 1) { throw \"Package '$packageFamilyName' application '$applicationId' must declare exactly one execution alias.\" }",
            "$alias = [string]$aliases[0].Alias",
            "if ($alias -notmatch '^clawctl(?:-[a-z0-9-]{1,15})?\\.exe$') { throw \"Package '$packageFamilyName' has an unexpected control alias.\" }",
            "$aliasPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Microsoft\\WindowsApps\\' + $packageFamilyName + '\\' + $alias)",
            "if (-not (Test-Path -LiteralPath $aliasPath -PathType Leaf)) { throw \"Package-qualified control alias '$aliasPath' is unavailable. Run clawctl gateway-service install after repairing the package registration.\" }",
            "$start = New-Object System.Diagnostics.ProcessStartInfo",
            "$start.FileName = $aliasPath",
            $"$start.Arguments = '{ControlArguments}'",
            "$start.WorkingDirectory = (Get-Location).Path",
            "$start.UseShellExecute = $false",
            "$start.CreateNoWindow = $true",
            "$process = [System.Diagnostics.Process]::Start($start)",
            "try { $process.WaitForExit(); exit $process.ExitCode } finally { $process.Dispose() }",
            string.Empty);
    }

    public static bool UpgradeLegacyActivationScript(
        string activationScriptPath,
        string packageFamilyName,
        Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationScriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentNullException.ThrowIfNull(log);

        string existing;
        try
        {
            existing = File.ReadAllText(activationScriptPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            log(
                "The retained gateway recovery script could not be inspected; " +
                $"this invocation will be treated as recovery: {DiagnosticFailure.Describe(exception)}");
            return true;
        }

        if (!string.Equals(
                existing,
                CreateLegacyActivationScript(packageFamilyName, LegacyControlArguments),
                StringComparison.Ordinal))
        {
            return false;
        }

        string temporaryPath =
            $"{activationScriptPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                CreateActivationScript(packageFamilyName),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, activationScriptPath, overwrite: true);
            log("Upgraded the retained gateway recovery script.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            log(
                "The retained gateway recovery script could not be upgraded; " +
                $"this invocation will still be treated as recovery: {DiagnosticFailure.Describe(exception)}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                log(
                    "The temporary gateway recovery script could not be removed: " +
                    DiagnosticFailure.Describe(exception));
            }
        }

        return true;
    }

    // Recognition fixture for the shipped pre-marker script, not a launch lane.
    // Retire with the legacy invocation upgrade after those installations migrate.
    internal static string CreateLegacyActivationScript(
        string packageFamilyName,
        string controlArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlArguments);

        string applicationUserModelId =
            $"{packageFamilyName}!{ControlApplicationId}";
        return string.Join(
            "\r\n",
            PowerShellMarker,
            "$ErrorActionPreference = 'Stop'",
            $"$packageFamilyName = '{QuotePowerShell(packageFamilyName)}'",
            $"$applicationId = '{ControlApplicationId}'",
            $"$arguments = '{controlArguments}'",
            $"$applicationUserModelId = '{QuotePowerShell(applicationUserModelId)}'",
            $"$package = Get-AppxPackage -Name '{QuotePowerShell(GetPackageName(packageFamilyName))}' | " +
            "Where-Object { $_.PackageFamilyName -eq $packageFamilyName } | " +
            "Select-Object -First 1",
            "if ($null -eq $package) { throw \"Package '$packageFamilyName' is not registered for the current user.\" }",
            "if ($package.Status -inotmatch '^(Ok|Ready)$') { throw \"Package '$packageFamilyName' is not ready (status $($package.Status)).\" }",
            "$manifestPath = Join-Path $package.InstallLocation 'AppxManifest.xml'",
            "if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw \"Package '$packageFamilyName' has no AppxManifest.xml at $manifestPath.\" }",
            "[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw",
            "$application = @($manifest.Package.Applications.Application | Where-Object { $_.Id -eq $applicationId }) | Select-Object -First 1",
            "if ($null -eq $application) { throw \"Package '$packageFamilyName' does not declare application '$applicationId'.\" }",
            "if ([string]$application.Executable -ne 'openclaw.exe') { throw \"Package '$packageFamilyName' application '$applicationId' does not target openclaw.exe.\" }",
            "$source = @'",
            "using System;",
            "using System.ComponentModel;",
            "using System.Runtime.InteropServices;",
            "namespace OpenClaw.PackageActivation",
            "{",
            "    [ComImport]",
            "    [Guid(\"2e941141-7f97-4756-ba1d-9decde894a3d\")]",
            "    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]",
            "    public interface IApplicationActivationManager",
            "    {",
            "        [PreserveSig]",
            "        int ActivateApplication(",
            "            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,",
            "            [MarshalAs(UnmanagedType.LPWStr)] string arguments,",
            "            uint options,",
            "            out uint processId);",
            "    }",
            "",
            "    [ComImport]",
            "    [Guid(\"45ba127d-10a8-46ea-8ab7-56ea9078943c\")]",
            "    public class ApplicationActivationManager",
            "    {",
            "    }",
            "",
            "    public static class ApplicationActivator",
            "    {",
            "        private const uint Synchronize = 0x00100000;",
            "        private const uint QueryLimitedInformation = 0x1000;",
            "        private const uint Infinite = 0xffffffff;",
            "        private const uint WaitObject0 = 0;",
            "",
            "        [DllImport(\"kernel32.dll\", SetLastError = true)]",
            "        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);",
            "",
            "        [DllImport(\"kernel32.dll\", SetLastError = true)]",
            "        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);",
            "",
            "        [DllImport(\"kernel32.dll\", SetLastError = true)]",
            "        [return: MarshalAs(UnmanagedType.Bool)]",
            "        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);",
            "",
            "        [DllImport(\"kernel32.dll\")]",
            "        [return: MarshalAs(UnmanagedType.Bool)]",
            "        private static extern bool CloseHandle(IntPtr handle);",
            "",
            "        public static int ActivateAndWait(string appUserModelId, string arguments)",
            "        {",
            "            var manager =",
            "                (IApplicationActivationManager)new ApplicationActivationManager();",
            "            uint processId;",
            "            int result = manager.ActivateApplication(",
            "                appUserModelId,",
            "                arguments,",
            "                0,",
            "                out processId);",
            "            if (result != 0)",
            "            {",
            "                Marshal.ThrowExceptionForHR(result);",
            "            }",
            "",
            "            IntPtr process = OpenProcess(",
            "                Synchronize | QueryLimitedInformation,",
            "                false,",
            "                processId);",
            "            if (process == IntPtr.Zero)",
            "            {",
            "                throw new Win32Exception(Marshal.GetLastWin32Error());",
            "            }",
            "",
            "            try",
            "            {",
            "                uint exitCode;",
            "                if (WaitForSingleObject(process, Infinite) != WaitObject0 ||",
            "                    !GetExitCodeProcess(process, out exitCode))",
            "                {",
            "                    throw new Win32Exception(Marshal.GetLastWin32Error());",
            "                }",
            "",
            "                return checked((int)exitCode);",
            "            }",
            "            finally",
            "            {",
            "                CloseHandle(process);",
            "            }",
            "        }",
            "    }",
            "}",
            "'@",
            "Add-Type -TypeDefinition $source -Language CSharp",
            "$exitCode = [OpenClaw.PackageActivation.ApplicationActivator]::ActivateAndWait($applicationUserModelId, $arguments)",
            "exit $exitCode",
            string.Empty);
    }

    /// <summary>
    /// The Startup-folder fallback calls the same launcher rather than
    /// repeating its contents, so the two lanes cannot drift apart.
    /// </summary>
    public static string CreateFallback(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);

        // WSF explicitly selects JScript even when the user associates .js with
        // an editor. Its external script remains the same owner the task invokes.
        return string.Join(
            "\r\n",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<job>",
            $"<!-- {ScriptHostMarker} -->",
            $"<script language=\"JScript\" src=\"{System.Security.SecurityElement.Escape(launcherPath)}\" />",
            "</job>",
            string.Empty);
    }

    public static bool LooksGenerated(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Contains(Marker, StringComparison.Ordinal) ||
               content.Contains(PowerShellMarker, StringComparison.Ordinal) ||
               content.Contains(ScriptHostMarker, StringComparison.Ordinal);
    }

    private static string QuoteJavaScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static string QuotePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// A family name is <c>Name_PublisherId</c> and package names cannot
    /// contain underscores, so the name is everything before the last one.
    /// Deriving it binds each script to the identity that generated it, so a
    /// side-by-side development identity does not look up the base package.
    /// </summary>
    private static string GetPackageName(string packageFamilyName)
    {
        int separator = packageFamilyName.LastIndexOf('_');
        if (separator <= 0)
        {
            throw new ArgumentException(
                $"'{packageFamilyName}' is not a package family name.",
                nameof(packageFamilyName));
        }

        return packageFamilyName[..separator];
    }
}
