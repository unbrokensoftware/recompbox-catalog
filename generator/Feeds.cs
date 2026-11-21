using System.Text.Json;
using SM64EnhancedWindowsBuilder.Models;

namespace CatalogGenerator;

internal static class FeedType
{
    public const string Local = "local";
    public const string Url = "url";
    public const string RetComm = "retcomm";
    public const string GitHubTopics = "github-topics";
}

internal sealed record FeedFile
{
    /// <summary>A port whose project stops working stays listed (as last seen) this many days, then drops out until it works again.</summary>
    public int RemoveAfterDays { get; init; } = 7;
    public List<FeedConfig> Feeds { get; init; } = [];

    /// <summary>Outside lists the weekly harvest reads to find new projects. Only used to find GitHub repositories;
    /// every one is checked and written as our own entry.</summary>
    public List<DiscoverConfig> Discover { get; init; } = [];
}

internal sealed record DiscoverConfig
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Note { get; init; } = "";
}

/// <summary>health.json: ports that are currently failing, by id. Ports that work aren't listed.</summary>
internal sealed record PortHealth
{
    public DateTimeOffset FirstFailed { get; init; }
    public string LastError { get; init; } = "";
}

/// <summary>One place the generator pulls from. Fields not used by a feed's type are ignored.</summary>
internal sealed record FeedConfig
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Note { get; init; } = "";

    /// <summary>local: folder of entry files, relative to the repo root.</summary>
    public string Path { get; init; } = "";

    /// <summary>url: https address of a JSON file holding one entry or an array of entries.</summary>
    public string Url { get; init; } = "";

    /// <summary>retcomm: GitHub owner/name and branch of the catalog.</summary>
    public string Repo { get; init; } = "";
    public string Branch { get; init; } = "main";

    /// <summary>retcomm: source platform slug to LaunchBox platform name. Only listed platforms are imported.</summary>
    public Dictionary<string, string> Platforms { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>github-topics: topics to search, minimum stars, repos kept per topic and the report file.</summary>
    public List<string> Topics { get; init; } = [];
    public int MinStars { get; init; } = 10;
    public int MaxPerTopic { get; init; } = 50;
    public string Output { get; init; } = "";
}

/// <summary>An entry and where it came from. Strict entries are ours: rejecting one fails a --strict run.</summary>
internal sealed record SourceEntry(string Feed, string Origin, bool Strict, CatalogPort Entry);

internal static class Feeds
{
    public static async Task<List<SourceEntry>> LoadAsync(FeedConfig feed, string root, HttpClient web)
    {
        return feed.Type switch
        {
            FeedType.Local => LoadLocal(feed, root),
            FeedType.Url => await LoadUrlAsync(feed, web),
            FeedType.RetComm => await LoadRetCommAsync(feed, web),
            _ => throw new InvalidOperationException($"Feed {feed.Id}: unknown type '{feed.Type}'.")
        };
    }

    private static List<SourceEntry> LoadLocal(FeedConfig feed, string root)
    {
        string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, feed.Path));
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir))
            throw new InvalidOperationException($"Feed {feed.Id}: folder '{feed.Path}' not found inside the repo.");
        var list = new List<SourceEntry>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string origin = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (CatalogPort entry in Entries(File.ReadAllText(file), feed, origin, strict: true))
                list.Add(new SourceEntry(feed.Id, origin, true, entry));
        }
        return list;
    }

    private static async Task<List<SourceEntry>> LoadUrlAsync(FeedConfig feed, HttpClient web)
    {
        if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Feed {feed.Id}: url must be https.");
        string json = await web.GetStringAsync(uri);
        return Entries(json, feed, uri.ToString(), strict: false).Select(e => new SourceEntry(feed.Id, uri.ToString(), false, e)).ToList();
    }

    private static IEnumerable<CatalogPort> Entries(string json, FeedConfig feed, string origin, bool strict)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : [doc.RootElement];
        var result = new List<CatalogPort>();
        foreach (JsonElement item in items)
        {
            try
            {
                if (item.Deserialize<CatalogPort>(Program.ReadOptions) is { } entry) result.Add(entry);
            }
            catch (JsonException ex)
            {
                Program.Reject($"entry in {origin}", feed.Id, ex.Message, strict);
            }
        }
        return result;
    }

    // RetComM (github.com/TechnicallyComputers/retcomm-catalog): index.json lists ids per platform,
    // titles/<platform>/<id>.json holds one manifest. Read from raw files, so it costs no API calls.
    private static async Task<List<SourceEntry>> LoadRetCommAsync(FeedConfig feed, HttpClient web)
    {
        if (!Program.IsRepo(feed.Repo) || !Program.RepoPart(feed.Branch))
            throw new InvalidOperationException($"Feed {feed.Id}: repo must be owner/name and branch a plain name.");
        string baseUrl = $"https://raw.githubusercontent.com/{feed.Repo}/{feed.Branch}/";
        using var index = JsonDocument.Parse(await web.GetStringAsync(baseUrl + "index.json"));
        var list = new List<SourceEntry>();
        if (!index.RootElement.TryGetProperty("platforms", out JsonElement platforms)) return list;

        foreach ((string slug, string platformName) in feed.Platforms.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!platforms.TryGetProperty(slug, out JsonElement platform) ||
                !platform.TryGetProperty("titles", out JsonElement titles)) continue;
            string dir = platform.TryGetProperty("dir", out JsonElement d) ? d.GetString() ?? "" : $"titles/{slug}";
            if (!dir.Split('/').All(Program.RepoPart)) continue;
            foreach (JsonElement t in titles.EnumerateArray())
            {
                string id = t.GetString() ?? "";
                string origin = $"{feed.Repo}/{dir}/{id}.json";
                if (!Program.IsId(id)) { Program.Reject(origin, feed.Id, "bad id", false); continue; }
                using var manifest = JsonDocument.Parse(await web.GetStringAsync($"{baseUrl}{dir}/{id}.json"));
                if (MapRetComm(manifest.RootElement, platformName, out string? why) is { } port)
                    list.Add(new SourceEntry(feed.Id, origin, false, port));
                else
                    Program.Reject(origin, feed.Id, why!, false);
            }
        }
        return list;
    }

    private static CatalogPort? MapRetComm(JsonElement t, string platform, out string? why)
    {
        why = null;
        string repo = Str(t, "release", "github");
        string pattern = Str(t, "release", "asset_glob", "windows");
        if (!Program.IsRepo(repo)) { why = "no GitHub release repository"; return null; }
        if (pattern.Length == 0) { why = "no Windows release"; return null; }

        var roms = new List<CatalogRomFingerprint>();
        if (t.TryGetProperty("rom_identity", out JsonElement rom) && rom.ValueKind == JsonValueKind.Object)
        {
            if (rom.TryGetProperty("require_cue", out JsonElement cue) && cue.ValueKind == JsonValueKind.True)
            {
                why = "disc sets (CUE/BIN) are not supported by the installer yet";
                return null;
            }
            List<string> sha1 = Strings(rom, "sha1");
            List<long> sizes = rom.TryGetProperty("sizes", out JsonElement s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];
            for (int i = 0; i < sha1.Count; i++)
                roms.Add(new CatalogRomFingerprint { Label = "", Sha1 = sha1[i].ToLowerInvariant(), SizeBytes = sizes.Count == sha1.Count ? sizes[i] : 0 });
        }

        string name = Str(t, "name");
        string exe = Str(t, "launch", "windows");
        return new CatalogPort
        {
            Id = Str(t, "id"),
            Name = name,
            Game = name,
            ShortName = name,
            Platform = platform,
            Developer = repo.Split('/')[0],
            Kind = Str(t, "kind"),
            Description = Str(t, "description"),
            RepositoryUrl = "https://github.com/" + repo,
            Install = CatalogInstallKind.Download,
            Executable = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : "",
            Assets = [new CatalogAssetPattern { Pattern = pattern }],
            Roms = roms
        };
    }

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (string p in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return "";
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
