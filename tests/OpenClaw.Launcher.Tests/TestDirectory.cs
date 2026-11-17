namespace OpenClaw.Launcher.Tests;

internal static class TestDirectory
{
    public static string Create() =>
        Directory.CreateTempSubdirectory("openclaw-msix-host-tests-").FullName;
}
