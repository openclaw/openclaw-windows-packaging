using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenClaw.Launcher.Mxc;

internal sealed record MxcBackendProbe(
    bool IsolationSessionAvailable,
    string? Tier,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The MXC containment backend this package uses. Only IsolationSession is
/// supported; backend selection is explicit so a host that silently resolves a
/// different backend cannot masquerade as a managed OpenClaw session.
/// </summary>
internal enum MxcContainment
{
    IsolationSession
}

/// <summary>
/// An opaque, backend-issued sandbox identity. The full value must be persisted
/// and replayed verbatim: it carries the provisioning application identity and
/// backend routing prefix, so a bare instance GUID is not a substitute.
/// </summary>
internal readonly record struct MxcSandboxId
{
    private MxcSandboxId(string value, string backendPrefix)
    {
        Value = value;
        BackendPrefix = backendPrefix;
    }

    public string Value { get; }

    /// <summary>
    /// Leading segment of <see cref="Value"/> that selects the backend, such as
    /// <c>iso</c> for IsolationSession.
    /// </summary>
    public string BackendPrefix { get; }

    public const string IsolationSessionPrefix = "iso";

    public bool IsIsolationSession =>
        string.Equals(BackendPrefix, IsolationSessionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses a persisted or backend-returned identity. A missing or unknown
    /// prefix is a malformed identity, not an unknown backend to guess at.
    /// </summary>
    public static MxcSandboxId Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                "Sandbox id is empty.");
        }

        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new MxcException(
                MxcErrorCode.MalformedId,
                "Sandbox id must carry a backend prefix.");
        }

        return new MxcSandboxId(value, value[..separator]);
    }

    /// <summary>
    /// Reports the application this identity was issued to, when the backend
    /// encodes one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value stays opaque to this package: it is replayed verbatim and is
    /// never rebuilt from parts. This reads the owning application only so a
    /// caller can refuse an identity that provably belongs to someone else
    /// before asking the backend to act on it. A record naming this
    /// installation does not establish that the identity inside it does.
    /// </para>
    /// <para>
    /// False is returned whenever the payload cannot be read, including a
    /// backend or format this package does not recognize. Absence of evidence
    /// is not evidence of a foreign owner, and inventing a failure here would
    /// strand every existing session the moment the backend changed its
    /// encoding.
    /// </para>
    /// </remarks>
    public bool TryGetOwningApplicationId([NotNullWhen(true)] out string? applicationId)
    {
        applicationId = null;
        if (!IsIsolationSession)
        {
            return false;
        }

        int separator = Value.IndexOf(':', StringComparison.Ordinal);
        string payload = Value[(separator + 1)..];
        if (payload.Length == 0)
        {
            return false;
        }

        // The payload is base64url without padding.
        string normalized = payload.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            0 => normalized,
            _ => string.Empty
        };
        if (normalized.Length == 0)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("appId", out JsonElement appId) ||
                appId.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            applicationId = appId.GetString();
            return !string.IsNullOrWhiteSpace(applicationId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public override string ToString() => Value;
}

/// <summary>
/// Provision-time metadata reported by IsolationSession. The workspace path is
/// an ephemeral directory shared between the caller and the isolated agent user
/// and is removed when the sandbox is deprovisioned.
/// </summary>
internal sealed record MxcProvisionMetadata(
    string AgentUserName,
    string AgentUserSid,
    string EphemeralWorkspacePath);

/// <summary>
/// Request to create a sandbox. <paramref name="AppId"/> must be
/// <c>PFN:&lt;packageFamilyName&gt;</c> for a packaged caller; it is fixed for
/// the sandbox lifetime and is rejected on every later phase.
/// </summary>
internal sealed record MxcProvisionRequest(string AppId);

internal sealed record MxcProvisionResult(
    MxcSandboxId SandboxId,
    MxcProvisionMetadata? Metadata,
    string? CorrelationVector);

/// <summary>
/// A command to run inside a started sandbox.
/// </summary>
/// <remarks>
/// The backend accepts a single command-line string, not an argument vector, so
/// arbitrary OpenClaw arguments must never be interpolated here. Callers launch
/// a controlled guest helper and pass real arguments as request data.
/// </remarks>
internal sealed record MxcExecutionRequest(string CommandLine);

internal sealed record MxcExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// The MXC lifecycle operations this package requires, shaped after the
/// upcoming <c>Microsoft.Mxc.Sdk</c> state-aware API so the published SDK can
/// replace the transport without changing session or gateway behavior.
/// </summary>
internal interface IMxcSessionClient
{
    Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken);

    Task StartAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken);

    Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command with the caller's console attached, returning its exit
    /// code. Nothing is captured.
    /// </summary>
    /// <remarks>
    /// Interactive OpenClaw requires this: a buffered execution only returns
    /// after the child exits, so prompts would never reach the terminal and
    /// typed input would never reach the child. Because nothing is captured, a
    /// dispatch failure cannot be read back as a structured envelope, and the
    /// caller must establish the outcome from the guest helper's control
    /// result instead.
    /// </remarks>
    Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken);

    Task StopAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken);

    Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken);
}
