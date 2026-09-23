using System.Diagnostics.CodeAnalysis;
using Sdk = Microsoft.Mxc.Sdk;

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

    /// <summary>
    /// Translates an SDK failure into the classification this package acts on.
    /// </summary>
    /// <remarks>
    /// The backend's own remediation text names the recovery step more
    /// precisely than this package can infer from the code alone, so it is
    /// appended verbatim rather than replaced with a generic hint.
    /// </remarks>
    internal static MxcException FromSdk(Sdk.MxcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = exception.Message;
        if (exception.Remediation is { Length: > 0 } remediation)
        {
            message = $"{message} {remediation}";
        }

        if (exception.NativeCode is { Length: > 0 } nativeCode)
        {
            message = $"{message} (native code {nativeCode})";
        }

        return new MxcException(
            Classify(exception.Code),
            message,
            exception.Code.ToString(),
            exception);
    }

    // Anything unlisted stays Unknown and keeps its SDK code name rather than
    // being guessed into a recovery path the backend did not report.
    internal static MxcErrorCode Classify(Sdk.ErrorCode code) => code switch
    {
        Sdk.ErrorCode.MalformedId => MxcErrorCode.MalformedId,
        Sdk.ErrorCode.StaleId => MxcErrorCode.StaleId,
        Sdk.ErrorCode.PolicyValidation => MxcErrorCode.PolicyValidation,
        Sdk.ErrorCode.MalformedRequest or
        Sdk.ErrorCode.NullArgument or
        Sdk.ErrorCode.InvalidUtf8 => MxcErrorCode.MalformedRequest,
        Sdk.ErrorCode.UnsupportedContainment => MxcErrorCode.UnsupportedContainment,
        Sdk.ErrorCode.BackendError => MxcErrorCode.BackendError,
        Sdk.ErrorCode.BackendUnavailable => MxcErrorCode.RuntimeUnavailable,
        _ => MxcErrorCode.Unknown
    };
}
