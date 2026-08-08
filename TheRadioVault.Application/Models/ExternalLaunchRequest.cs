namespace TheRadioVault.Application.Models;

public enum ExternalLaunchKind
{
    OpenUri,
    OpenFile,
    OpenFolder,
    RevealFile,
    OpenTextFile,
    LaunchExecutable
}

public sealed record ExternalLaunchRequest(
    ExternalLaunchKind Kind,
    string Target,
    string? Arguments = null)
{
    public static ExternalLaunchRequest Uri(global::System.Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) throw new ArgumentException("The URI must be absolute.", nameof(uri));
        return new ExternalLaunchRequest(ExternalLaunchKind.OpenUri, uri.AbsoluteUri);
    }

    public static ExternalLaunchRequest File(string path) =>
        new(ExternalLaunchKind.OpenFile, RequireTarget(path));

    public static ExternalLaunchRequest Folder(string path) =>
        new(ExternalLaunchKind.OpenFolder, RequireTarget(path));

    public static ExternalLaunchRequest Reveal(string path) =>
        new(ExternalLaunchKind.RevealFile, RequireTarget(path));

    public static ExternalLaunchRequest TextFile(string path) =>
        new(ExternalLaunchKind.OpenTextFile, RequireTarget(path));

    public static ExternalLaunchRequest Executable(string path, string? arguments = null) =>
        new(ExternalLaunchKind.LaunchExecutable, RequireTarget(path), arguments);

    private static string RequireTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("A launch target is required.", nameof(target));
        return target.Trim();
    }
}
