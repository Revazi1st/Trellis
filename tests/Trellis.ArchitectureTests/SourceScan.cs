namespace Trellis.ArchitectureTests;

/// <summary>
/// The two halves of a source scan with an allowlist (<see cref="ConventionTests"/> and its siblings): the
/// hits outside the allowlist, and the allowlist entries that no longer excuse anything. An allowlist is
/// keyed by repository-relative path and each value is the reason; an entry whose file is gone, or no
/// longer trips the scan, is stale, and a stale entry fails like a hit, so the list stays true.
/// </summary>
internal static class SourceScan
{
    /// <summary>Every hit <paramref name="hits"/> finds in a file the allowlist does not name, as <c>path:line: text</c>.</summary>
    public static List<string> Violations(
        IEnumerable<SourceFile> files, IReadOnlyDictionary<string, string> allowlist, Func<SourceFile, IEnumerable<int>> hits)
        => [.. files
            .Where(file => !allowlist.ContainsKey(file.RelativePath))
            .SelectMany(file => hits(file).Select(file.Describe))];

    /// <summary>The allowlist entries naming no scanned file, or a file in which <paramref name="hits"/> finds nothing now.</summary>
    public static List<string> StaleEntries(
        IEnumerable<SourceFile> files, IReadOnlyDictionary<string, string> allowlist, Func<SourceFile, IEnumerable<int>> hits)
    {
        Dictionary<string, SourceFile> scanned = files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        return [.. allowlist.Keys
            .Where(path => !scanned.TryGetValue(path, out SourceFile? file) || !hits(file).Any())
            .Order(StringComparer.Ordinal)
            .Select(static path => $"{path}: on the allowlist, but the scan no longer finds it there; remove the entry")];
    }
}
