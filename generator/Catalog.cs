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
    // The game's LaunchBox Games Database id, for box art when its name doesn't find it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LaunchBoxDbId { get; init; }
    public string Developer { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
    public string Requirement { get; init; } = "";
    public IReadOnlyList<string> Features { get; init; } = [];
    public bool Pick { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Verified { get; init; }
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
    // Settings files and folders kept, like the saves folder, when the port is updated or repaired.
    public IReadOnlyList<string> Keep { get; init; } = [];
    // Settings written on install only when the file isn't there yet, so the player's own changes stay.
    public IReadOnlyList<CatalogDefaultFile> DefaultFiles { get; init; } = [];
    // The port's mod list file (N64ModernRuntime's mods.json). RecompBox puts its mods first in the order listed
    // in the catalog, which is the texture priority, and turns on the ones the game hasn't seen yet.
    public string ModOrderFile { get; init; } = "";
    // A program from the release run once after every install, like building the game's assets from the ROM.
    public CatalogSetup? Setup { get; init; }
    public IReadOnlyList<CatalogRelease> Releases { get; init; } = [];
    public IReadOnlyList<CatalogAssetPattern> Assets { get; init; } = [];
    public IReadOnlyList<CatalogRomFingerprint> Roms { get; init; } = [];
    public IReadOnlyList<string> SourceTypes { get; init; } = [];
    public IReadOnlyList<string> LaunchArgs { get; init; } = [];
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

internal sealed record CatalogDefaultFile
{
    public string Path { get; init; } = "";
    public string Content { get; init; } = "";
}

internal sealed record CatalogSetup
{
    // Relative to the install folder; a .exe, .cmd or .bat from the release.
    public string Run { get; init; } = "";
    // "{rom}" is replaced with the ROM's path in the install folder.
    public IReadOnlyList<string> Args { get; init; } = [];
    // A file the setup must produce for the install to count.
    public string Creates { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool StopWhenCreated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AnswerPrompts { get; init; }
    public IReadOnlyList<System.Text.Json.Nodes.JsonObject> Requests { get; init; } = [];
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
    // The mod's own id in the game, for the port's ModOrderFile.
    public string LoadId { get; init; } = "";
    // For a mod the author only offers from their own page (no Url): the page to download it from and the file's
    // name. RecompBox opens the page and imports the file the player downloads, checked against Sha256.
    public string Page { get; init; } = "";
    public string FileName { get; init; } = "";

    [JsonIgnore]
    public bool IsManual => Url.Length == 0 && Page.Length > 0;

    [JsonIgnore]
    public bool IsLibrary => Category.Equals(CatalogModCategory.Library, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Catalog))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
