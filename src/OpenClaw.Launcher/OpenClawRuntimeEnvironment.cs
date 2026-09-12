namespace OpenClaw.Launcher;

/// <summary>
/// The environment every OpenClaw process this package starts must receive.
/// </summary>
/// <remarks>
/// These three variables are what tell OpenClaw that this package owns
/// supervision, repair, and updates. They are built in one place so a
/// foreground launch, a session launch, a supervisor, and a logon launch cannot
/// drift apart and leave one path where OpenClaw supervises or updates itself.
/// </remarks>
internal static class OpenClawRuntimeEnvironment
{
    public const string SupervisorModeVariable = "OPENCLAW_SUPERVISOR_MODE";
    public const string ServiceRepairPolicyVariable = "OPENCLAW_SERVICE_REPAIR_POLICY";
    public const string NoAutoUpdateVariable = "OPENCLAW_NO_AUTO_UPDATE";

    public const string ExternalValue = "external";
    public const string NoAutoUpdateValue = "1";

    /// <summary>
    /// The variables to apply, as an ordinary dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SupervisorModeVariable] = ExternalValue,
            [ServiceRepairPolicyVariable] = ExternalValue,
            [NoAutoUpdateVariable] = NoAutoUpdateValue,
        };

    /// <summary>
    /// Applies the variables to a process environment.
    /// </summary>
    public static void ApplyTo(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach ((string name, string value) in Build())
        {
            environment[name] = value;
        }
    }
}
