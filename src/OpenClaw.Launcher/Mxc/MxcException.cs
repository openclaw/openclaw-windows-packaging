namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Error classifications this package acts on. Unrecognized backend codes map
/// to <see cref="Unknown"/> and keep their original text in
/// <see cref="MxcException.BackendCode"/> rather than being reshaped into a
/// code that implies a recovery path the backend did not report.
/// </summary>
public enum MxcErrorCode
{
    Unknown,

    /// <summary>The persisted identity is not a well-formed sandbox id.</summary>
    MalformedId,

    /// <summary>The sandbox named by a well-formed id no longer exists.</summary>
    StaleId,

    /// <summary>The backend rejected the requested policy.</summary>
    PolicyValidation,

    /// <summary>The MXC runtime is missing, unusable, or unsupported here.</summary>
    RuntimeUnavailable,

    /// <summary>The runtime produced output this package cannot interpret.</summary>
    ProtocolViolation
}

public sealed class MxcException : Exception
{
    public MxcException(
        MxcErrorCode code,
        string message,
        string? backendCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        BackendCode = backendCode;
    }

    public MxcErrorCode Code { get; }

    /// <summary>
    /// The verbatim backend error code, preserved even when
    /// <see cref="Code"/> is <see cref="MxcErrorCode.Unknown"/>, so diagnostics
    /// report what the runtime actually said.
    /// </summary>
    public string? BackendCode { get; }

    internal static MxcErrorCode Classify(string? backendCode) => backendCode switch
    {
        "malformed_id" => MxcErrorCode.MalformedId,
        "stale_id" => MxcErrorCode.StaleId,
        "policy_validation" => MxcErrorCode.PolicyValidation,
        _ => MxcErrorCode.Unknown
    };
}
