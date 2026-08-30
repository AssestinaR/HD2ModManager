using System.Text.Json;

namespace HD2ModCore.Infrastructure;

// Community-facing manifest model. Unknown fields intentionally remain ignored.
internal sealed class StandardModManifest
{
    public string? Guid { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? IconPath { get; init; }
    public List<StandardModManifestOption> Options { get; init; } = [];
    public List<StandardModManifestNode>? Nodes { get; init; }

    public static StandardModManifest? TryLoad(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<StandardModManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            });
            // A manager-only Nodes manifest is handled by the legacy identity restore path.
            return manifest is { Name: not null } || manifest?.Options.Count > 0 || manifest?.IconPath is not null ? manifest : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}

internal sealed class StandardModManifestOption
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Image { get; init; }
    public List<string>? Include { get; init; }
    // Community manifests traditionally called the next level "subOptions".
    // Keeping the same recursive shape means arbitrary option depth is parsed
    // without inventing a manager-only nesting limit.
    public List<StandardModManifestOption>? SubOptions { get; init; }
}

internal sealed class StandardModManifestNode
{
    public string? RelativePath { get; init; }
    public string? Guid { get; init; }
    public string? Name { get; init; }
    public string? Notes { get; init; }
}

// Stored beside an imported option node so the relationship remains recoverable
// even if the library snapshot or the original package manifest is lost.
internal sealed class OptionRelationDocument
{
    public int Version { get; init; }
    public string? Kind { get; init; }
    public List<string> HostModGuids { get; init; } = [];
	public string? SourcePackageGuid { get; init; }
	public string? SourcePath { get; init; }
	public int? OptionOrder { get; init; }
	public bool SuppressDefaultDirectOptionActivation { get; init; }
}
