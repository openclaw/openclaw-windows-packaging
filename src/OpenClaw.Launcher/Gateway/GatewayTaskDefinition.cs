using System.Xml;
using System.Xml.Linq;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// The settings of a registered logon task that this installation cares about.
/// </summary>
/// <remarks>
/// Drift is compared field by field rather than by comparing XML text, because
/// Task Scheduler normalizes what it stores: it reorders elements, fills in
/// defaults, and rewrites the principal. A text comparison would therefore
/// report drift on every single probe.
/// </remarks>
internal sealed record GatewayTaskSnapshot(
    string UserId,
    string LogonType,
    string RunLevel,
    bool Enabled,
    bool HasSingleLogonTrigger,
    bool LogonTriggerEnabled,
    string LogonTriggerUserId,
    bool HasSingleExecAction,
    string MultipleInstancesPolicy,
    bool DisallowStartIfOnBatteries,
    bool StopIfGoingOnBatteries,
    string ExecutionTimeLimit,
    string Command,
    string Arguments);

/// <summary>
/// Builds the logon task this installation registers.
/// </summary>
internal static class GatewayTaskDefinition
{
    private const string TaskNamespace =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// No execution time limit. The gateway is long-running, and Task
    /// Scheduler's default would terminate it after 72 hours.
    /// </summary>
    public const string NoExecutionTimeLimit = "PT0S";

    /// <summary>
    /// A second logon trigger must not start a second gateway. The existing
    /// instance is authoritative, so the new one is dropped rather than
    /// queued or allowed to run beside it.
    /// </summary>
    public const string IgnoreNewInstances = "IgnoreNew";

    public static GatewayTaskSnapshot CreateSnapshot(
        string userSid,
        string commandProcessorPath,
        string launcherPath) =>
        new(
            UserId: userSid,
            // InteractiveToken runs with the user's interactive session and
            // desktop but without storing a password, which is what a per-user
            // logon task needs.
            LogonType: "InteractiveToken",
            RunLevel: "LeastPrivilege",
            Enabled: true,
            HasSingleLogonTrigger: true,
            LogonTriggerEnabled: true,
            LogonTriggerUserId: userSid,
            HasSingleExecAction: true,
            MultipleInstancesPolicy: IgnoreNewInstances,
            // A gateway that refuses to start on battery, or dies when the
            // charger is unplugged, is a laptop user losing their agent for a
            // reason they will never connect to power.
            DisallowStartIfOnBatteries: false,
            StopIfGoingOnBatteries: false,
            ExecutionTimeLimit: NoExecutionTimeLimit,
            Command: commandProcessorPath,
            Arguments: $"/d /c \"\"{launcherPath}\"\"");

    public static string CreateXml(GatewayTaskSnapshot snapshot, string taskName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        XNamespace ns = TaskNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(
                ns + "Task",
                new XAttribute("version", "1.3"),
                new XElement(
                    ns + "RegistrationInfo",
                    new XElement(ns + "URI", "\\" + taskName),
                    new XElement(
                        ns + "Description",
                        "Starts the OpenClaw gateway when this user signs in.")),
                new XElement(
                    ns + "Triggers",
                    new XElement(
                        ns + "LogonTrigger",
                        new XElement(ns + "Enabled", Bool(snapshot.LogonTriggerEnabled)),
                        new XElement(ns + "UserId", snapshot.LogonTriggerUserId))),
                new XElement(
                    ns + "Principals",
                    new XElement(
                        ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", snapshot.UserId),
                        new XElement(ns + "LogonType", snapshot.LogonType),
                        new XElement(ns + "RunLevel", snapshot.RunLevel))),
                new XElement(
                    ns + "Settings",
                    new XElement(ns + "Enabled", Bool(snapshot.Enabled)),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(
                        ns + "MultipleInstancesPolicy",
                        snapshot.MultipleInstancesPolicy),
                    new XElement(
                        ns + "DisallowStartIfOnBatteries",
                        Bool(snapshot.DisallowStartIfOnBatteries)),
                    new XElement(
                        ns + "StopIfGoingOnBatteries",
                        Bool(snapshot.StopIfGoingOnBatteries)),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(
                        ns + "ExecutionTimeLimit",
                        snapshot.ExecutionTimeLimit),
                    new XElement(ns + "Priority", "7"),
                    new XElement(
                        ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false"))),
                new XElement(
                    ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        ns + "Exec",
                        new XElement(ns + "Command", snapshot.Command),
                        new XElement(ns + "Arguments", snapshot.Arguments)))));

        using var writer = new StringWriter();
        using (var xml = XmlWriter.Create(
            writer,
            new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            document.Save(xml);
        }

        return writer.ToString();
    }

    /// <summary>
    /// Reads a registered task back into a snapshot. Returns false when the XML
    /// cannot be understood, which is reported as unreadable rather than as a
    /// missing or mismatched task.
    /// </summary>
    public static bool TryParse(
        string xml,
        out GatewayTaskSnapshot? snapshot,
        out string? detail)
    {
        snapshot = null;
        detail = null;

        try
        {
            XNamespace ns = TaskNamespace;
            XElement root = XDocument.Parse(xml).Root
                ?? throw new InvalidOperationException("The task XML is empty.");

            XElement? principal = root
                .Element(ns + "Principals")?
                .Elements(ns + "Principal")
                .FirstOrDefault();
            List<XElement> logonTriggers = root
                .Element(ns + "Triggers")?
                .Elements(ns + "LogonTrigger")
                .ToList() ?? [];
            List<XElement> execActions = root
                .Element(ns + "Actions")?
                .Elements(ns + "Exec")
                .ToList() ?? [];
            XElement? settings = root.Element(ns + "Settings");
            XElement? trigger = logonTriggers.Count == 1 ? logonTriggers[0] : null;
            XElement? action = execActions.Count == 1 ? execActions[0] : null;

            snapshot = new GatewayTaskSnapshot(
                UserId: Text(principal, ns + "UserId"),
                LogonType: Text(principal, ns + "LogonType"),
                RunLevel: Text(principal, ns + "RunLevel", "LeastPrivilege"),
                Enabled: Flag(settings, ns + "Enabled", true),
                HasSingleLogonTrigger: logonTriggers.Count == 1,
                LogonTriggerEnabled: Flag(trigger, ns + "Enabled", true),
                LogonTriggerUserId: Text(trigger, ns + "UserId"),
                HasSingleExecAction: execActions.Count == 1,
                MultipleInstancesPolicy: Text(
                    settings,
                    ns + "MultipleInstancesPolicy",
                    "IgnoreNew"),
                DisallowStartIfOnBatteries: Flag(
                    settings,
                    ns + "DisallowStartIfOnBatteries",
                    true),
                StopIfGoingOnBatteries: Flag(
                    settings,
                    ns + "StopIfGoingOnBatteries",
                    true),
                ExecutionTimeLimit: Text(
                    settings,
                    ns + "ExecutionTimeLimit",
                    "PT72H"),
                Command: Text(action, ns + "Command"),
                Arguments: Text(action, ns + "Arguments", string.Empty));
            return true;
        }
        catch (Exception ex) when (
            ex is XmlException or InvalidOperationException or FormatException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Text(
        XElement? parent,
        XName name,
        string fallback = "") =>
        parent?.Element(name)?.Value.Trim() ?? fallback;

    private static bool Flag(XElement? parent, XName name, bool fallback)
    {
        string? value = parent?.Element(name)?.Value.Trim();
        return value is null
            ? fallback
            : bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }
}
