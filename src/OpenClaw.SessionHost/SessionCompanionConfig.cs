using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed record CompanionConfigPatch(
    CompanionGatewayPatch Gateway, JsonElement ExpectedGateway, string ExpectedHash, string ExpectedPath);
internal sealed record CompanionGatewayPatch(string Mode, int Port, string Bind, CompanionAuthPatch Auth);
internal sealed record CompanionAuthPatch(string Mode, string Token);
internal sealed record CompanionConfigSnapshot(JsonElement Gateway, string Path, string Hash, bool Exists);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionConfigPatch))]
[JsonSerializable(typeof(CompanionConfigSnapshot))]
internal sealed partial class CompanionConfigPatchContext : JsonSerializerContext;

internal static class SessionCompanionConfig
{
    private const string ConfigurationOperation = """
        import { readFile } from "node:fs/promises";
        import { resolve } from "node:path";
        import { pathToFileURL } from "node:url";
        import { isDeepStrictEqual } from "node:util";

        const [applicationDirectory, expectedPath, patchPath] = process.argv.slice(1);
        const sdkUrl = (name) => pathToFileURL(
          resolve(applicationDirectory, "dist", "plugin-sdk", `${name}.js`),
        ).href;
        const assertEnvironment = () => {
          for (const name of [
            "OPENCLAW_CONFIG_PATH", "OPENCLAW_STATE_DIR", "OPENCLAW_HOME", "OPENCLAW_PROFILE",
            "OPENCLAW_GATEWAY_URL", "OPENCLAW_GATEWAY_PORT", "OPENCLAW_GATEWAY_TOKEN",
            "OPENCLAW_GATEWAY_PASSWORD",
          ]) {
            if (process.env[name]?.trim()) {
              throw new Error(`The agent account has a ${name} override. Remove it before preparing Companion.`);
            }
          }
        };
        const assertSnapshot = (snapshot) => {
          assertEnvironment();
          if (!snapshot.valid ||
              resolve(snapshot.path).toLowerCase() !== resolve(expectedPath).toLowerCase()) {
            throw new Error("The agent Gateway configuration is invalid or resolves to another profile.");
          }
        };

        assertEnvironment();
        if (patchPath) {
          const patch = JSON.parse(await readFile(patchPath, "utf8"));
          const { mutateConfigFile } = await import(sdkUrl("config-mutation"));
          await mutateConfigFile({
            base: "source",
            baseHash: patch.expectedHash,
            writeOptions: { expectedConfigPath: patch.expectedPath },
            afterWrite: { mode: "none", reason: "The package owns the Gateway lifecycle." },
            mutate(draft, { snapshot }) {
              assertSnapshot(snapshot);
              // The root hash alone cannot detect a changed included Gateway.
              if (!isDeepStrictEqual(snapshot.config.gateway ?? {}, patch.expectedGateway)) {
                throw new Error("Gateway configuration changed while Companion prepared it. Retry setup.");
              }
              draft.gateway ??= {};
              const auth = { ...draft.gateway.auth, ...patch.gateway.auth };
              Object.assign(draft.gateway, patch.gateway, { auth });
            },
          });
        } else {
          const { readConfigFileSnapshot } = await import(sdkUrl("health"));
          const snapshot = await readConfigFileSnapshot({
            observe: false,
            recoverSuspicious: false,
            pluginValidation: "core-only",
          });
          assertSnapshot(snapshot);
          process.stdout.write(JSON.stringify({
            gateway: snapshot.config.gateway ?? {},
            path: snapshot.path,
            hash: snapshot.hash,
            exists: snapshot.exists,
          }));
        }
        """;

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        string? profileRoot = null,
        Func<SessionCompanionConfigRequest, string, int>? applyPatch = null,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)>? readEffectiveConfiguration = null,
        Func<IDisposable>? enterState = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        try
        {
            SessionCompanionConfigRequest request =
                SessionCompanionConfigProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            enterState ??= SessionStateAccess.ForAgent().EnterReader;
            using IDisposable state = enterState();
            string configPath = Path.Combine(profileRoot ?? AgentProfile.GetPath(), ".openclaw", "openclaw.json");
            SessionCompanionConfigResult result = Configure(
                request, configPath, writeFile,
                applyPatch ?? ((r, patch) => ApplyPatch(r, configPath, patch)),
                readEffectiveConfiguration ?? (r => ReadEffectiveConfiguration(r, configPath)));
            writeFile(resultPath, SessionCompanionConfigProtocol.SerializeResult(result));
            return 0;
        }
        catch (Exception exception) when (exception is SessionLaunchException or IOException or
            UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            writeFile(resultPath, SessionCompanionConfigProtocol.SerializeResult(
                new SessionCompanionConfigResult { RequestId = requestId, Error = exception.Message }));
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    internal static SessionCompanionConfigResult Configure(
        SessionCompanionConfigRequest request,
        string configPath,
        Action<string, string> writeFile,
        Func<SessionCompanionConfigRequest, string, int> applyPatch,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)> readEffectiveConfiguration)
    {
        CompanionConfigSnapshot snapshot = ReadSnapshot(request, configPath, readEffectiveConfiguration);
        (int? existingPort, string? existingToken) = ReadGatewayConfiguration(snapshot.Gateway);
        if (request.CheckOnly)
        {
            if (!snapshot.Exists)
            {
                throw new SessionLaunchException(
                    "The agent's Gateway configuration is missing. Run `clawctl companion prepare` first.");
            }

            if (existingPort is null || string.IsNullOrWhiteSpace(existingToken))
            {
                throw new SessionLaunchException(
                    "The agent's Gateway configuration has no usable port or token. Run `clawctl companion prepare` first.");
            }

            return new SessionCompanionConfigResult
            {
                RequestId = request.RequestId,
                Port = existingPort.Value,
                Token = existingToken
            };
        }

        if (existingPort is not null && !string.IsNullOrWhiteSpace(existingToken))
        {
            return new SessionCompanionConfigResult
            {
                RequestId = request.RequestId,
                Port = existingPort.Value,
                Token = existingToken
            };
        }

        int port = existingPort ?? request.Port;
        string token = existingToken ?? request.Token ??
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string patchPath = Path.Combine(
            Path.GetDirectoryName(configPath)!,
            ".companion-config-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            writeFile(patchPath, JsonSerializer.Serialize(
                new CompanionConfigPatch(new CompanionGatewayPatch(
                    "local", port, "loopback", new CompanionAuthPatch("token", token)),
                    snapshot.Gateway, snapshot.Hash, snapshot.Path),
                CompanionConfigPatchContext.Default.CompanionConfigPatch));
            int exitCode = applyPatch(request, patchPath);
            if (exitCode != 0)
            {
                throw new SessionLaunchException(
                    $"OpenClaw rejected the Companion Gateway configuration (exit code {exitCode}). " +
                    "It may have changed during preparation. Retry setup, or inspect it with " +
                    "`clawctl pwsh` and `openclaw config validate`.");
            }
        }
        finally
        {
            File.Delete(patchPath);
        }

        CompanionConfigSnapshot configured = ReadSnapshot(request, configPath, readEffectiveConfiguration);
        if (!configured.Exists)
        {
            throw new SessionLaunchException("OpenClaw did not create the agent's Gateway configuration.");
        }
        (int? configuredPort, string? configuredToken) =
            ReadGatewayConfiguration(configured.Gateway);
        if (configuredPort != port || configuredToken != token)
        {
            throw new SessionLaunchException(
                "The agent's Gateway configuration changed while Companion prepared it. Retry setup.");
        }
        return new SessionCompanionConfigResult { RequestId = request.RequestId, Port = port, Token = token };
    }

    private static CompanionConfigSnapshot ReadSnapshot(
        SessionCompanionConfigRequest request,
        string configPath,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)> readEffectiveConfiguration)
    {
        (int exitCode, string output) = readEffectiveConfiguration(request);
        if (exitCode != 0)
        {
            throw new SessionLaunchException(
                $"OpenClaw could not read the effective Gateway configuration (exit code {exitCode}). " +
                "Check agent profile and Gateway environment overrides with `clawctl pwsh`, " +
                "then run `openclaw config validate`.");
        }

        CompanionConfigSnapshot? snapshot = JsonSerializer.Deserialize(
            output, CompanionConfigPatchContext.Default.CompanionConfigSnapshot);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Hash) ||
            !string.Equals(snapshot.Path, configPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "OpenClaw did not return a snapshot of the expected agent Gateway configuration.");
        }
        return snapshot;
    }

    private static (int? Port, string? Token) ReadGatewayConfiguration(JsonElement gateway)
    {
        if (gateway.ValueKind != JsonValueKind.Object)
        {
            throw new SessionLaunchException("The agent's effective Gateway configuration is not an object.");
        }
        if (!gateway.EnumerateObject().Any())
        {
            return (null, null);
        }
        if (!gateway.TryGetProperty("mode", out JsonElement mode) ||
            mode.ValueKind != JsonValueKind.String || mode.GetString() != "local")
        {
            throw new SessionLaunchException(
                "The agent's existing Gateway mode is not local. Reconfigure it explicitly.");
        }
        if (gateway.TryGetProperty("bind", out JsonElement bind) &&
            (bind.ValueKind != JsonValueKind.String || bind.GetString() != "loopback"))
        {
            throw new SessionLaunchException(
                "The agent's existing Gateway bind is not loopback. Reconfigure it explicitly.");
        }
        int? port = null;
        if (gateway.TryGetProperty("port", out JsonElement configuredPort))
        {
            if (!configuredPort.TryGetInt32(out int value) || value is < 1 or > 65535)
            {
                throw new SessionLaunchException("The agent's existing Gateway port is invalid.");
            }
            port = value;
        }
        bool hasAuth = gateway.TryGetProperty("auth", out JsonElement auth);
        if (hasAuth && auth.ValueKind != JsonValueKind.Object)
        {
            throw new SessionLaunchException("The agent's Gateway authentication is not an object.");
        }
        JsonElement authMode = default;
        if (hasAuth && auth.TryGetProperty("mode", out authMode) &&
            (authMode.ValueKind != JsonValueKind.String || authMode.GetString() != "token"))
        {
            throw new SessionLaunchException("The agent's Gateway uses a different authentication mode.");
        }
        if (authMode.ValueKind == JsonValueKind.Undefined &&
            (HasPassword(auth) ||
                (gateway.TryGetProperty("remote", out JsonElement remote) && HasPassword(remote))))
        {
            throw new SessionLaunchException("The agent's Gateway implicitly uses password authentication.");
        }
        if (!hasAuth || !auth.TryGetProperty("token", out JsonElement authToken))
        {
            return (port, null);
        }
        if (authToken.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(authToken.GetString()) ||
            authToken.GetString()!.Contains("${", StringComparison.Ordinal) ||
            authToken.GetString() == "__OPENCLAW_REDACTED__")
        {
            throw new SessionLaunchException(
                "OpenClaw did not expose the existing Gateway token to Companion. " +
                "The existing configuration was not changed.");
        }
        return (port, authToken.GetString());
    }

    private static bool HasPassword(JsonElement settings)
        => settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("password", out JsonElement password) &&
            password.ValueKind != JsonValueKind.Null &&
            (password.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(password.GetString()));

    private static int ApplyPatch(SessionCompanionConfigRequest request, string configPath, string patchPath)
        => RunNode(request,
            ["--input-type=module", "--eval", ConfigurationOperation,
                request.ApplicationDirectory!, configPath, patchPath]).ExitCode;

    private static (int ExitCode, string Output) ReadEffectiveConfiguration(
        SessionCompanionConfigRequest request, string configPath)
        => RunNode(
            request,
            ["--input-type=module", "--eval", ConfigurationOperation, request.ApplicationDirectory!, configPath]);

    private static (int ExitCode, string Output) RunNode(
        SessionCompanionConfigRequest request,
        IReadOnlyList<string> arguments)
    {
        List<string> nodeArguments = [];
        if (request.NativeRootPath is not null)
        {
            nodeArguments.AddRange(["--import", new Uri(request.PreloadPath!).AbsoluteUri]);
        }
        nodeArguments.AddRange(arguments);
        SessionProcessOutput output = SessionProcessLauncher.Capture(new SessionLaunchRequest
        {
            Executable = request.NodePath,
            Arguments = nodeArguments,
            WorkingDirectory = AppContext.BaseDirectory,
            Environment = request.Environment,
            NativeRootPath = request.NativeRootPath
        }, TimeSpan.FromMinutes(2));
        return (output.ExitCode, output.StandardOutput);
    }
}
