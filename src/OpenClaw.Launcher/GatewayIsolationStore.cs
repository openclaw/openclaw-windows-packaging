using System.Text.Json;

namespace OpenClaw.Launcher;

public sealed record GatewayIsolationState(bool Enabled)
{
    public static GatewayIsolationState Disabled { get; } = new(false);
}

public sealed class GatewayIsolationStore
{
    private const string FileName = "gateway-isolation.json";
    private readonly string _path;

    public GatewayIsolationStore(string? localApplicationData = null)
    {
        string root = localApplicationData ?? Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _path = Path.Combine(root, "OpenClawGatewayMSIX", FileName);
    }

    internal GatewayIsolationStore(string exactPath, bool useExactPath) => _path = exactPath;

    public GatewayIsolationState Read()
    {
        if (!File.Exists(_path))
        {
            return GatewayIsolationState.Disabled;
        }

        try
        {
            return JsonSerializer.Deserialize<GatewayIsolationState>(
                File.ReadAllText(_path)) ??
                throw new InvalidDataException("The isolation state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The launcher isolation state at '{_path}' is invalid.",
                exception);
        }
    }

    public void Write(GatewayIsolationState state)
    {
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The isolation state path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
