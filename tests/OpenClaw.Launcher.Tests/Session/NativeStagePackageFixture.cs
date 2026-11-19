using System.Security.Cryptography;
using System.Text.Json;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Lays out an installed package the way MSIX does: <c>app</c> beside
/// <c>payload/payload-files.json</c>.
/// </summary>
internal static class NativeStagePackageFixture
{
    /// <summary>Writes each file under <c>app</c>, keyed by its '/'-separated path.</summary>
    public static string WriteApplication(
        string packageRoot,
        IReadOnlyDictionary<string, string> files,
        bool withInventory = true)
    {
        string application = Path.Combine(packageRoot, "app");
        if (Directory.Exists(application))
        {
            Directory.Delete(application, recursive: true);
        }

        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "openclaw.mjs"), "fixture");
        foreach ((string path, string content) in files)
        {
            string target = Path.Combine(application, path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        if (withInventory)
        {
            WriteInventory(packageRoot);
        }

        return application;
    }

    /// <summary>Creates a directory junction, which needs no symbolic-link privilege.</summary>
    public static void CreateJunction(string link, string target)
    {
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mklink /J failed with exit code {process.ExitCode}.");
        }
    }

    /// <summary>Writes the inventory Build-MSIX produces from the files on disk.</summary>
    public static void WriteInventory(string packageRoot)
    {
        string application = Path.Combine(packageRoot, "app");
        string inventory = Path.Combine(packageRoot, "payload", "payload-files.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
        using FileStream stream = File.Create(inventory);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("files");
        foreach (string file in Directory
            .EnumerateFiles(application, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(application, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(application, file.Replace('/', '\\')));
            writer.WriteStartObject();
            writer.WriteString("path", file);
            writer.WriteNumber("length", bytes.LongLength);
            writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
