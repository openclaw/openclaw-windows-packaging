using System.Globalization;

namespace OpenClaw.SessionHost;

/// <summary>How a recorded native stage compares with the installed application.</summary>
internal enum NativeStageState
{
    /// <summary>Every inventoried file is present with the expected bytes, and nothing else is.</summary>
    Current,

    /// <summary>No root is recorded, or the recorded root or its marker is gone.</summary>
    Missing,

    /// <summary>The stage was built from different package content.</summary>
    StaleSource,

    /// <summary>Built from this content, but files have since been removed or added.</summary>
    Incomplete,

    /// <summary>Built from this content, but a file's bytes have since changed.</summary>
    ContentMismatch,

    /// <summary>The stage contains a link rather than the agent's own copy.</summary>
    UnsupportedLayout,

    /// <summary>The stage could not be read.</summary>
    Inaccessible,
}

/// <summary>The outcome of verifying one recorded native stage.</summary>
internal sealed record NativeStageCheck(
    NativeStageState State,
    NativeStageInventory? Expected,
    string? ObservedContentId)
{
    public int VerifiedFileCount { get; init; }

    public int MissingFileCount { get; init; }

    public int ExtraFileCount { get; init; }

    public int MismatchedFileCount { get; init; }

    /// <summary>Kebab-case state name shared by diagnostics and failure text.</summary>
    public string StateName => Name(State);

    internal static string Name(NativeStageState state) => state switch
    {
        NativeStageState.Current => "current",
        NativeStageState.Missing => "missing",
        NativeStageState.StaleSource => "stale-source",
        NativeStageState.Incomplete => "incomplete",
        NativeStageState.ContentMismatch => "content-mismatch",
        NativeStageState.UnsupportedLayout => "unsupported-layout",
        _ => "inaccessible"
    };

    /// <summary>A bounded clause completing "the staged native dependencies ...".</summary>
    public string Describe()
    {
        string counts = Expected is null
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"; {VerifiedFileCount} of {Expected.Files.Count} files verified, " +
                $"{MissingFileCount} missing, {ExtraFileCount} unexpected, {MismatchedFileCount} changed");
        string reason = State switch
        {
            NativeStageState.Missing when Expected is not null =>
                "are missing although this package requires them",
            NativeStageState.StaleSource when Expected is null =>
                "are recorded although this package carries none",
            NativeStageState.StaleSource =>
                $"were staged from different package content (expected {Expected!.ContentId}, " +
                $"found {ObservedContentId ?? "none"})",
            NativeStageState.Incomplete => "no longer contain exactly the files this package requires",
            NativeStageState.ContentMismatch => "have changed since they were staged",
            NativeStageState.UnsupportedLayout => "contain a link instead of staged files",
            _ => "could not be read"
        };
        return $"{reason} [{StateName}{counts}]";
    }
}
