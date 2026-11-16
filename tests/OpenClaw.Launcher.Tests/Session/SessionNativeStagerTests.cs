using System.Text.Json;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Covers the mirroring that makes packaged native addons loadable by the
/// isolated-session identity.
/// </summary>
/// <remarks>
/// These tests use ordinary files rather than real addons: the staging
/// contract is about which directories are copied and when, not about loading
/// native code.
/// </remarks>
public sealed class SessionNativeStagerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"koffi\index.js", "koffi")]
    [InlineData(@"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node", "koffi")]
    [InlineData(@"@openclaw\fs-safe\native.node", @"@openclaw\fs-safe")]
    [InlineData(@"@openclaw\fs-safe\node_modules\@openclaw\inner\x.node", @"@openclaw\fs-safe")]
    public void OwningPackageIsTheOutermostPackageDirectory(
        string relativePath,
        string expected)
    {
        Assert.True(
            SessionNativeStager.TryGetOwningPackage(relativePath, out string package));
        Assert.Equal(expected, package);
    }

    [Theory]
    [InlineData("koffi")]
    [InlineData(@"@openclaw")]
    [InlineData("")]
    public void LooseEntriesHaveNoOwningPackage(string relativePath)
    {
        Assert.False(
            SessionNativeStager.TryGetOwningPackage(relativePath, out _));
    }

    [Fact]
    public void DiscoveryFindsOnlyPackagesCarryingNativeArtifacts()
    {
        string modules = Path.Combine(_root, "app", "node_modules");
        WriteFile(modules, @"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node");
        WriteFile(modules, @"@openclaw\fs-safe\native.node");
        WriteFile(modules, @"sqlite-vec\node_modules\sqlite-vec-windows-x64\vec0.dll");
        WriteFile(modules, @"node-pty\prebuilds\conpty\OpenConsole.exe");
        WriteFile(modules, @"lodash\index.js");
        WriteFile(modules, @"react\index.js");

        IReadOnlyList<string> discovered =
            SessionNativeStager.DiscoverNativePackages(modules);

        Assert.Equal(
            [@"@openclaw\fs-safe", "koffi", "node-pty", "sqlite-vec"],
            discovered);
    }

    /// <summary>
    /// The discovery contract that keeps this fix from going stale: a package
    /// nobody listed anywhere is staged because it carries a native artifact.
    /// </summary>
    [Fact]
    public void DiscoveryFindsPackagesNoListNames()
    {
        string modules = Path.Combine(_root, "app", "node_modules");
        WriteFile(modules, @"brand-new-upstream-dep\build\Release\addon.node");

        Assert.Equal(
            ["brand-new-upstream-dep"],
            SessionNativeStager.DiscoverNativePackages(modules));
    }

    [Fact]
    public void StageMirrorsTheWholeOwningPackageAndLeavesOtherPackagesBehind()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\index.js", "loader");
        WriteFile(modules, @"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node", "native");
        WriteFile(modules, @"lodash\index.js", "js only");

        string staged = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;

        // The sibling JavaScript comes along, because the package's own files
        // locate the binaries next to them.
        Assert.Equal(
            "loader",
            File.ReadAllText(Path.Combine(staged, "node_modules", "koffi", "index.js")));
        Assert.Equal(
            "native",
            File.ReadAllText(Path.Combine(
                staged,
                @"node_modules\koffi\node_modules\@koromix\koffi-win32-x64\koffi.node")));
        Assert.False(
            Directory.Exists(Path.Combine(staged, "node_modules", "lodash")),
            "A package without native artifacts must keep running from the package.");
    }

    /// <summary>
    /// Reuse is decided by verifying the stage, and a verified stage is not
    /// copied again: the copy would rewrite the files a running gateway maps.
    /// </summary>
    [Fact]
    public void AVerifiedStageIsReusedWithoutBeingCopiedAgain()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string staged = Path.Combine(first, "node_modules", "koffi", "koffi.node");
        DateTime sentinel = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(staged, sentinel);

        string second = SessionNativeStager.Stage(application, local)!;

        Assert.Equal(first, second);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(staged));
    }

    [Fact]
    public void ChangedPackageContentStagesToANewRootAndRemovesTheOldOne()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        WriteFile(modules, @"koffi\koffi.node", "a different native payload");
        string second = SessionNativeStager.Stage(application, local)!;

        Assert.NotEqual(first, second);
        Assert.False(
            Directory.Exists(first),
            "An upgrade must not leave the superseded copy behind.");
        Assert.Equal(
            "a different native payload",
            File.ReadAllText(Path.Combine(second, "node_modules", "koffi", "koffi.node")));
    }

    /// <summary>
    /// Content identity must come from bytes. Size and write time are not
    /// enough: a republished package can keep both.
    /// </summary>
    [Fact]
    public void ChangedContentAtTheSameLengthAndTimestampStagesAgain()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        string native = Path.Combine(modules, @"koffi\koffi.node");
        WriteFile(modules, @"koffi\koffi.node", "native-aaa");
        string local = Path.Combine(_root, "local");
        DateTime written = File.GetLastWriteTimeUtc(native);

        string first = SessionNativeStager.Stage(application, local)!;

        File.WriteAllText(native, "native-bbb");
        File.SetLastWriteTimeUtc(native, written);
        Assert.Equal(written, File.GetLastWriteTimeUtc(native));

        string second = SessionNativeStager.Stage(application, local)!;

        Assert.NotEqual(first, second);
        Assert.Equal(
            "native-bbb",
            File.ReadAllText(Path.Combine(second, @"node_modules\koffi\koffi.node")));
    }

    /// <summary>
    /// A superseded root can still be the one a running gateway loads from.
    /// Reclaiming it must be all-or-nothing, never a partial delete that
    /// removes that process's unlocked files.
    /// </summary>
    [Fact]
    public void ASupersededRootStillInUseIsLeftIntact()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        WriteFile(modules, @"koffi\index.js", "loader");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string stagedNative = Path.Combine(first, @"node_modules\koffi\koffi.node");
        string stagedLoader = Path.Combine(first, @"node_modules\koffi\index.js");

        // Stands in for the mapped native image a running process holds.
        using (File.Open(stagedNative, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            string second = SessionNativeStager.Stage(application, local)!;

            Assert.NotEqual(first, second);
            Assert.True(
                File.Exists(stagedLoader),
                "Reclaiming an in-use root must not delete the files around the "
                    + "locked one.");
            Assert.Equal("loader", File.ReadAllText(stagedLoader));
            Assert.Equal("native", File.ReadAllText(stagedNative));
        }
    }

    /// <summary>
    /// A consumer need not have any staged file open. An agent shell sitting
    /// at a prompt has the redirect in its environment and nothing mapped, yet
    /// every OpenClaw it later runs resolves through that root.
    /// </summary>
    [Fact]
    public void ASupersededRootWithALiveConsumerAndNoOpenFilesIsLeftIntact()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;

        using (FileStream? lease = SessionNativeStager.OpenConsumerLease(first))
        {
            Assert.NotNull(lease);

            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            string second = SessionNativeStager.Stage(application, local)!;

            Assert.NotEqual(first, second);
            Assert.Equal(
                "native",
                File.ReadAllText(Path.Combine(first, @"node_modules\koffi\koffi.node")));
        }
    }

    /// <summary>
    /// The retry the reclamation promises. After the upgrade, every later setup
    /// sees unchanged content, so reclamation has to run on that path too or
    /// releasing the consumer never frees the root.
    /// </summary>
    [Fact]
    public void AReleasedRootIsReclaimedByTheNextUnchangedContentSetup()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string second;
        using (SessionNativeStager.OpenConsumerLease(first))
        {
            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            second = SessionNativeStager.Stage(application, local)!;
            Assert.True(Directory.Exists(first));
        }

        // Content is unchanged now, so this is the reuse path.
        Assert.Equal(second, SessionNativeStager.Stage(application, local));
        Assert.False(
            Directory.Exists(first),
            "Releasing the consumer must let the next setup reclaim the root.");
    }

    [Fact]
    public void ACurrentStageIsAdmittedAndHeldUntilTheLeaseIsReleased()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");
        string root = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;

        using (FileStream? lease = SessionNativeStager.Admit(application, root))
        {
            Assert.NotNull(lease);
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }

        Directory.Move(root, root + "-moved");
    }

    [Fact]
    public void ANativeFreeApplicationIsAdmittedOnlyWithoutARecordedRoot()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"lodash\index.js");
        string elsewhere = Path.Combine(_root, "recorded");
        Directory.CreateDirectory(elsewhere);

        Assert.Null(SessionNativeStager.Admit(application, null));
        AssertStale(() => SessionNativeStager.Admit(application, elsewhere), "stale-source");
    }

    /// <summary>
    /// A payload that gained native dependencies after setup recorded none
    /// must not run with them resolving to package content it cannot load.
    /// </summary>
    [Fact]
    public void ANativeApplicationIsRefusedWithoutAStage()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");

        AssertStale(() => SessionNativeStager.Admit(application, null), "missing");
        AssertStale(
            () => SessionNativeStager.Admit(application, Path.Combine(_root, "absent")),
            "missing");
    }

    [Fact]
    public void AStageFromDifferentPackageContentIsRefused()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native a");
        string old = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;

        WriteFile(modules, @"koffi\koffi.node", "native b");
        WriteFile(modules, @"fs-safe\dist\helper.exe", "helper");

        AssertStale(() => SessionNativeStager.Admit(application, old), "stale-source");
    }

    public static TheoryData<string, string> MarkerRetainedDamage => new()
    {
        { "delete-helper", "[incomplete;" },
        { "truncate-native", "[content-mismatch;" },
        { "same-length-same-time", "[content-mismatch;" },
        { "extra-executable", "[incomplete;" },
    };

    /// <summary>
    /// The completion marker proves a copy once finished. Each of these
    /// changes leaves it in place and must still stop the launch, and setup
    /// must then repair the stage rather than reuse it.
    /// </summary>
    [Theory]
    [MemberData(nameof(MarkerRetainedDamage))]
    public void DamageBehindAnIntactMarkerIsRefusedAndRepairedBySetup(string damage, string state)
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"fs-safe\dist\helper.exe", "helper");
        WriteFile(modules, @"fs-safe\dist\worker.js", "worker");
        WriteFile(modules, @"fs-safe-win32-x64\fs.node", "native-aaa");
        string local = Path.Combine(_root, "local");
        string root = SessionNativeStager.Stage(application, local)!;
        string native = Path.Combine(root, @"node_modules\fs-safe-win32-x64\fs.node");
        switch (damage)
        {
            case "delete-helper":
                File.Delete(Path.Combine(root, @"node_modules\fs-safe\dist\helper.exe"));
                break;
            case "truncate-native":
                File.WriteAllText(native, "native");
                break;
            case "same-length-same-time":
                DateTime written = File.GetLastWriteTimeUtc(native);
                File.WriteAllText(native, "native-bbb");
                File.SetLastWriteTimeUtc(native, written);
                break;
            default:
                WriteFile(Path.Combine(root, "node_modules"), @"fs-safe\dist\unexpected.exe", "planted");
                break;
        }

        Assert.True(File.Exists(Path.Combine(root, ".staged-content-id")));
        AssertStale(() => SessionNativeStager.Admit(application, root), state);

        string repaired = SessionNativeStager.Stage(application, local)!;

        using FileStream? lease = SessionNativeStager.Admit(application, repaired);
        Assert.NotNull(lease);
        Assert.Equal(
            "native-aaa",
            File.ReadAllText(Path.Combine(repaired, @"node_modules\fs-safe-win32-x64\fs.node")));
        Assert.False(File.Exists(Path.Combine(repaired, @"node_modules\fs-safe\dist\unexpected.exe")));
    }

    /// <summary>
    /// The lease protects only the stage's own marker. A stage whose
    /// node_modules is a link into another stage would verify that stage's
    /// bytes while leaving it unprotected from reclamation.
    /// </summary>
    [Fact]
    public void AStageWhoseNodeModulesIsALinkIsRefused()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");
        string root = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;
        string moved = Path.Combine(_root, "elsewhere", "node_modules");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(Path.Combine(root, "node_modules"), moved);
        string link = Path.Combine(root, "node_modules");
        NativeStagePackageFixture.CreateJunction(link, moved);
        try
        {
            AssertStale(() => SessionNativeStager.Admit(application, root), "unsupported-layout");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ALinkInsideTheStageIsRefused()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");
        string root = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;
        string target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        string link = Path.Combine(root, @"node_modules\koffi\linked");
        NativeStagePackageFixture.CreateJunction(link, target);
        try
        {
            AssertStale(() => SessionNativeStager.Admit(application, root), "unsupported-layout");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// Earlier releases named stage roots by the same content identifier. A
    /// drift here would make every upgraded installation restage and lose
    /// the ability to recognize its existing root.
    /// </summary>
    [Fact]
    public void ThePackageInventoryAndASourceScanAgreeOnTheStageIdentity()
    {
        var files = new Dictionary<string, string>
        {
            ["node_modules/@openclaw/fs-safe/dist/helper.exe"] = "helper",
            ["node_modules/@openclaw/fs-safe/package.json"] = "{}",
            ["node_modules/koffi/node_modules/@koromix/koffi-win32-x64/koffi.node"] = "native",
            ["node_modules/koffi/index.js"] = "loader",
            ["node_modules/lodash/index.js"] = "js only",
        };
        string withInventory = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "packaged"), files);
        string withoutInventory = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "unpackaged"), files, withInventory: false);

        NativeStageInventory packaged = NativeStageInventory.Load(withInventory)!;
        NativeStageInventory scanned = NativeStageInventory.Load(withoutInventory)!;

        Assert.Equal(NativeStageProvenance.PackageInventory, packaged.Provenance);
        Assert.Equal(NativeStageProvenance.UnpackagedSource, scanned.Provenance);
        Assert.Equal(scanned.ContentId, packaged.ContentId);
        Assert.Equal([@"@openclaw\fs-safe", "koffi"], packaged.Packages);
        Assert.Equal(4, packaged.Files.Count);
    }

    /// <summary>
    /// The package inventory is trusted to name expected bytes, so content
    /// that disagrees with it is a damaged installation, not a stage to keep.
    /// </summary>
    [Fact]
    public void StagingRefusesPackageContentThatDisagreesWithItsInventory()
    {
        string application = NativeStagePackageFixture.WriteApplication(
            Path.Combine(_root, "packaged"),
            new Dictionary<string, string> { ["node_modules/koffi/koffi.node"] = "native" });
        File.WriteAllText(Path.Combine(application, @"node_modules\koffi\koffi.node"), "altered");
        string local = Path.Combine(_root, "local");

        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(
            () => SessionNativeStager.Stage(application, local));

        Assert.Contains("payload inventory", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(local, "OpenClawGatewayMSIX", SessionNativeStager.DirectoryName)));
    }

    [Theory]
    [InlineData("node_modules/../escape/x.node")]
    [InlineData("/node_modules/koffi/x.node")]
    [InlineData(@"node_modules\koffi\x.node")]
    public void AnInventoryNamingAnUnsafePathIsRejected(string path)
    {
        string packageRoot = Path.Combine(_root, "packaged");
        string application = NativeStagePackageFixture.WriteApplication(
            packageRoot,
            new Dictionary<string, string> { ["node_modules/koffi/koffi.node"] = "native" });
        string inventory = Path.Combine(packageRoot, "payload", "payload-files.json");
        File.WriteAllText(
            inventory,
            "{\"files\":[{\"path\":" + JsonSerializer.Serialize(path) +
            ",\"length\":1,\"sha256\":\"" + new string('0', 64) + "\"}]}");

        Assert.Throws<SessionLaunchException>(() => NativeStageInventory.Load(application));
    }

    private static void AssertStale(Func<FileStream?> admit, string state)
    {
        SessionLaunchException exception = Assert.Throws<SessionLaunchException>(() => admit()?.Dispose());
        Assert.Equal(SessionLaunchProtocol.NativeStageStaleErrorCode, exception.Code);
        Assert.Contains(state, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Run `clawctl setup`", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplicationWithoutNativeArtifactsStagesNothing()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"lodash\index.js");

        Assert.Null(SessionNativeStager.Stage(application, Path.Combine(_root, "local")));
    }

    [Fact]
    public void AnApplicationWithoutModulesStagesNothing()
    {
        string application = Path.Combine(_root, "app");
        Directory.CreateDirectory(application);

        Assert.Null(SessionNativeStager.Stage(application, Path.Combine(_root, "local")));
    }

    private static void WriteFile(
        string modulesDirectory,
        string relativePath,
        string content = "x")
    {
        string path = Path.Combine(modulesDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
