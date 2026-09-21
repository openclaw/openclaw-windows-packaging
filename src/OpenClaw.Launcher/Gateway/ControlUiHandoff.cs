using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher.Gateway;

internal sealed record ControlUiHandoff(bool Ok, string? BrowserUrl);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ControlUiHandoff))]
internal sealed partial class ControlUiHandoffJsonContext : JsonSerializerContext;

internal static class ControlUiHandoffParser
{
    internal static bool TryParse(
        string output,
        IReadOnlyCollection<int> observedPorts,
        out string? browserUrl)
    {
        browserUrl = null;
        try
        {
            ControlUiHandoff? handoff = JsonSerializer.Deserialize(
                output,
                ControlUiHandoffJsonContext.Default.ControlUiHandoff);
            if (handoff is null || !handoff.Ok ||
                string.IsNullOrWhiteSpace(handoff.BrowserUrl) ||
                !Uri.TryCreate(handoff.BrowserUrl, UriKind.Absolute, out Uri? candidate) ||
                (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
                !candidate.IsLoopback ||
                !observedPorts.Contains(candidate.Port))
            {
                return false;
            }

            browserUrl = handoff.BrowserUrl;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
