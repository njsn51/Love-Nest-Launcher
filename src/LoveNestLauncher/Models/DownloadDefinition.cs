namespace LoveNestLauncher.Models;

public sealed record DownloadDefinition(
    int Index,
    string Name,
    string Url,
    string FileName,
    string FileType,
    string Category,
    string Description);

public sealed record SourceLoadResult(
    IReadOnlyList<DownloadDefinition> Items,
    bool IsCustom,
    string SourceLabel,
    string SourcePath);

public sealed class SourceImportResult
{
    public required int Count { get; init; }
    public required string FilePath { get; init; }
}
