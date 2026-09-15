using System.Diagnostics.CodeAnalysis;

namespace OpenClaw.Launcher.Mxc;

/// <summary>
/// Error classifications this package acts on. Unrecognized backend codes map
/// to <see cref="Unknown"/> and keep their original text in
/// <see cref="MxcException.BackendCode"/> rather than being reshaped into a
/// code that implies a recovery path the backend did not report.
/// </summary>
internal enum MxcErrorCode
{
    Unknown,

    /// <summary>The persisted identity is not a well-formed sandbox id.</summary>
    MalformedId,

    /// <summary>The sandbox named by a well-formed id no longer exists.</summary>
    StaleId,

    /// <summary>The backend rejected the requested policy.</summary>
    PolicyValidation,

    /// <summary>
    /// The backend could not parse or accept the request this package built.
    /// This indicates a defect here, not a recoverable runtime condition.
    /// </summary>
    MalformedRequest,

    /// <summary>No state-aware backend is registered for the id's prefix.</summary>
    UnsupportedContainment,

    /// <summary>
    /// The backend attempted the operation and failed, e.g. exec against a
    /// session that is provisioned but not started.
    /// </summary>
    BackendError,

    /// <summary>The MXC runtime is missing, unusable, or unsupported here.</summary>
    RuntimeUnavailable,

    /// <summary>The runtime produced output this package cannot interpret.</summary>
    ProtocolViolation
}

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification =
        "Every MXC failure carries the classified error code and the verbatim " +
        "backend code that the error-handling paths switch on. A parameterless " +
        "or message-only constructor would let a caller create an exception " +
        "with no classification, which is precisely the state this type exists " +
        "to prevent.")]
internal sealed class MxcException : Exception
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

    // Codes observed from @microsoft/mxc-sdk 0.8.0 wxc-exec.exe against the
    // Windows IsolationSession backend. Anything unlisted stays Unknown and
    // keeps its verbatim text rather than being guessed into a recovery path.
    internal static MxcErrorCode Classify(string? backendCode) => backendCode switch
    {
        "malformed_id" => MxcErrorCode.MalformedId,
        "stale_id" => MxcErrorCode.StaleId,
        "policy_validation" => MxcErrorCode.PolicyValidation,
        "malformed_request" => MxcErrorCode.MalformedRequest,
        "unsupported_containment" => MxcErrorCode.UnsupportedContainment,
        "backend_error" => MxcErrorCode.BackendError,
        _ => MxcErrorCode.Unknown
    };
}
