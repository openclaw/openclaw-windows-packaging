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
    ProtocolViolation,

    /// <summary>A caller configuration conflicts with the package-bound runtime.</summary>
    ConfigurationRejected
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
        Exception? innerException = null,
        string? operation = null)
        : base(message, innerException)
    {
        Code = code;
        BackendCode = backendCode;
        Operation = operation;
    }

    public MxcErrorCode Code { get; }

    /// <summary>
    /// The verbatim backend error code, preserved even when
    /// <see cref="Code"/> is <see cref="MxcErrorCode.Unknown"/>, so diagnostics
    /// report what the runtime actually said.
    /// </summary>
    public string? BackendCode { get; }

    /// <summary>
    /// The backend API call that failed, e.g.
    /// <c>IsoSessionOps.StartSessionAsync</c>. It is diagnostic detail, not a
    /// contract: MXC derives it from OS API names that may change.
    /// </summary>
    public string? Operation { get; }

}
