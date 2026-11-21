using System.Text.Json;
using System.Text.Json.Serialization;

namespace SM64EnhancedWindowsBuilder.Models;

internal sealed record Catalog
{
    public int SchemaVersion { get; init; }
    public DateTimeOffset Generated { get; init; }
    public IReadOnlyList<CatalogPort> Ports { get; init; } = [];
}

internal sealed record CatalogPort
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Game { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string Platform { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string Developer { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
    public string Requirement { get; init; } = "";
    public IReadOnlyList<string> Features { get; init; } = [];
    public bool Pick { get; init; }
    public string RepositoryUrl { get; init; } = "";
    public string License { get; init; } = "";
    public int Stars { get; init; }
    public DateTimeOffset Added { get; init; }
    public DateTimeOffset Updated { get; init; }
    public IReadOnlyList<string> Colors { get; init; } = [];
    public string Install { get; init; } = CatalogInstallKind.Download;
    public string Recipe { get; init; } = "";
    public string RomFile { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Saves { get; init; } = "";
    public string ModsFolder { get; init; } = "";
    // Empty file created in the install folder after every install, e.g. portable.txt so the port keeps
    // its settings, saves and mods next to the exe.
    public string PortableFile { get; init; } = "";
    public IReadOnlyList<CatalogRelease> Releases { get; init; } = [];
    public IReadOnlyList<CatalogAssetPattern> Assets { get; init; } = [];
    public IReadOnlyList<CatalogRomFingerprint> Roms { get; init; } = [];
    public IReadOnlyList<CatalogMod> Mods { get; init; } = [];

    [JsonIgnore]
    public CatalogRelease? Latest => Releases.Count > 0 ? Releases[0] : null;

    // The mods people choose from. Libraries are installed only when a chosen mod requires them.
    [JsonIgnore]
    public IEnumerable<CatalogMod> Choices => Mods.Where(m => !m.IsLibrary);
}

internal static class CatalogInstallKind
{
    public const string Download = "download";
    public const string Build = "build";
}

internal static class CatalogFeature
{
    public const string Widescreen = "widescreen";
    public const string HighFps = "high-fps";
    public const string HdTextures = "hd-textures";
    public const string Mods = "mods";
    public const string Online = "online";
    public const string Gyro = "gyro";
    public const string Randomizer = "randomizer";
}

internal static class CatalogModCategory
{
    public const string Graphics = "graphics";
    public const string Audio = "audio";
    public const string Controls = "controls";
    public const string QualityOfLife = "quality-of-life";
    public const string Characters = "characters";
    public const string RestoredContent = "restored-content";
    public const string Conversion = "conversion";
    public const string Library = "library";
}

internal sealed record CatalogRelease
{
    public string Version { get; init; } = "";
    public DateTimeOffset Published { get; init; }
    public long SizeBytes { get; init; }
    public bool Prerelease { get; init; }
}

internal sealed record CatalogAssetPattern
{
    public string Pattern { get; init; } = "";
    public string Os { get; init; } = "windows";
    public string Arch { get; init; } = "x64";

    public bool Matches(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName,
            "^" + System.Text.RegularExpressions.Regex.Escape(Pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

internal sealed record CatalogRomFingerprint
{
    public string Label { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long SizeBytes { get; init; }
}

internal sealed record CatalogMod
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Adds { get; init; } = "";
    public string Category { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public bool DefaultOn { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<string> Requires { get; init; } = [];
    public IReadOnlyList<string> Features { get; init; } = [];

    [JsonIgnore]
    public bool IsLibrary => Category.Equals(CatalogModCategory.Library, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Catalog))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
