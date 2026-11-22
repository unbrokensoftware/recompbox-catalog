using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SM64EnhancedWindowsBuilder.Models;

namespace CatalogGenerator;

/// <summary>
/// --harvest: reads the outside lists in feeds.json "discover", merges what they say about each GitHub repository,
/// keeps console games only, checks each repository has a Windows .zip or .7z release, and adds the ones we don't
/// list yet to sources/discovered/{platform}.json as our own entries. Existing entries are never changed.
/// </summary>
internal static partial class Harvest
{
    private const int Parallel = 6;

    public static async Task<int> RunAsync(string root)
    {
        FeedFile feeds = JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(Path.Combine(root, "feeds.json")), Program.ReadOptions) ?? new();
        (HttpClient api, HttpClient web, Program.GitHub github) = Program.CreateClients(root);
        using var apiScope = api;
        using var webScope = web;

        // Everything we already list, by repository and id.
        var knownRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        string sourcesDir = Path.Combine(root, "sources");
        foreach (string file in Directory.EnumerateFiles(sourcesDir, "*.json", SearchOption.AllDirectories))
            foreach (CatalogPort p in ReadEntries(file))
            {
                knownIds.Add(p.Id);
                if (RepoOf(p.RepositoryUrl) is { } r) knownRepos.Add(r);
            }

        knownRepos.UnionWith(Program.Blocked(root));

        var hints = new Dictionary<string, Hint>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoverConfig source in feeds.Discover.Where(d => d.Enabled))
        {
            int before = hints.Count;
            string json = await web.GetStringAsync(source.Url);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (Hint h in Read(source.Type, doc.RootElement))
            {
                if (h.Repo is null || !Program.IsRepo(h.Repo)) continue;
                hints[h.Repo] = hints.TryGetValue(h.Repo, out Hint? had) ? had.Merge(h) : h;
            }
            Console.WriteLine($"Discover {source.Id}: {hints.Count - before} new repositories ({hints.Count} total).");
        }

        var todo = hints.Values.Where(h => !knownRepos.Contains(h.Repo!) && !h.Excluded && Platforms.Name(h) is not null)
            .OrderBy(h => h.Repo, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"{todo.Count} console repositories to check ({hints.Count - todo.Count} known, excluded or not a console game).");

        var added = new List<CatalogPort>();
        var skipped = new List<string>();
        var listedBefore = new HashSet<string>(knownRepos, StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(Parallel);
        var tasks = todo.Select(async h =>
        {
            await gate.WaitAsync();
            try
            {
                (CatalogPort? port, string? why) = await VerifyAsync(github, h);
                lock (added)
                {
                    if (port is not null && !knownRepos.Contains(RepoOf(port.RepositoryUrl)!))
                    {
                        knownRepos.Add(RepoOf(port.RepositoryUrl)!);
                        added.Add(port);
                    }
                    else if (why is not null) skipped.Add($"{h.Repo}: {why}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                lock (added) skipped.Add($"{h.Repo}: {ex.Message}");
            }
            finally { gate.Release(); }
        }).ToList();
        int done = 0;
        foreach (Task t in tasks)
        {
            await t;
            if (++done % 100 == 0) Console.WriteLine($"Checked {done}/{tasks.Count}, {added.Count} usable.");
        }

        added = DropReuploads(DropForks(added, listedBefore, skipped), skipped);

        // Ids are the repository name; on a clash the owner goes in front.
        foreach (CatalogPort port in added.OrderBy(p => p.RepositoryUrl, StringComparer.OrdinalIgnoreCase).ToList())
        {
            string id = port.Id;
            if (!knownIds.Add(id))
            {
                id = Slug(RepoOf(port.RepositoryUrl)!.Replace('/', '-'));
                if (!knownIds.Add(id)) { skipped.Add($"{port.RepositoryUrl}: id clash"); added.Remove(port); continue; }
            }
            added[added.IndexOf(port)] = port with { Id = id };
        }

        var writer = new JsonSerializerOptions(Program.ReadOptions) { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };
        string outDir = Path.Combine(sourcesDir, "discovered");
        Directory.CreateDirectory(outDir);
        foreach (var group in added.GroupBy(p => Slug(p.Platform)))
        {
            string file = Path.Combine(outDir, group.Key + ".json");
            var existing = File.Exists(file) ? ReadEntries(file).ToList() : [];
            var all = existing.Concat(group.Select(Clean)).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            Program.Write(file, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all, writer) + "\n"));
        }
        Program.Write(Path.Combine(root, "candidates", "harvest-skipped.md"), Encoding.UTF8.GetBytes(
            "# Harvest: not added\n\nRepositories the harvest found but couldn't use. Written by the harvest job.\n\n" +
            string.Concat(skipped.Order(StringComparer.OrdinalIgnoreCase).Select(s => "- " + s.Replace("\n", " ") + "\n"))));
        Console.WriteLine($"Added {added.Count} ports; {skipped.Count} skipped.");
        return 0;
    }

    // Only the fields an entry needs; the generator fills stars, releases and dates.
    private static CatalogPort Clean(CatalogPort p) => p with { Stars = 0, Added = default, Updated = default, Releases = [], License = "" };


    private static IEnumerable<CatalogPort> ReadEntries(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = new List<CatalogPort>();
        foreach (JsonElement item in items)
            try { if (item.Deserialize<CatalogPort>(Program.ReadOptions) is { } p) list.Add(p); }
            catch (JsonException) { }
        return list;
    }

    private static async Task<(CatalogPort? Port, string? Why)> VerifyAsync(Program.GitHub github, Hint h)
    {
        JsonElement? info = await github.GetAsync($"repos/{h.Repo}");
        if (info is not { } r) return (null, "repository not found");
        string repo = r.GetProperty("full_name").GetString() ?? h.Repo!;
        if (r.TryGetProperty("private", out var priv) && priv.GetBoolean()) return (null, "private");
        // Remember what a fork came from; it's dropped later only if that project is listed or added.
        if (r.TryGetProperty("fork", out var fork) && fork.GetBoolean())
            foreach (string up in new[] { "parent", "source" })
                if (r.TryGetProperty(up, out var p) && p.ValueKind == JsonValueKind.Object && p.GetProperty("full_name").GetString() is { } upstream)
                    lock (ForkOf)
                    {
                        if (!ForkOf.TryGetValue(repo, out List<string>? ups)) ForkOf[repo] = ups = [];
                        ups.Add(upstream);
                    }
        int stars = r.GetProperty("stargazers_count").GetInt32();

        JsonElement? list = await github.GetAsync($"repos/{repo}/releases?per_page=20");
        string? asset = null, pattern = null;
        foreach (bool stable in new[] { true, false })
        {
            foreach (JsonElement rel in list?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
            {
                if (rel.GetProperty("draft").GetBoolean() || rel.GetProperty("prerelease").GetBoolean() == stable) continue;
                string tag = rel.GetProperty("tag_name").GetString() ?? "";
                var names = rel.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? "").ToList();
                // A glob from a source list wins when it matches; else the best-looking Windows download.
                asset = h.WindowsGlob is { } glob && IsArchivePattern(glob) && Glob(glob) is { } g ? names.FirstOrDefault(g.IsMatch) : null;
                pattern = asset is not null ? h.WindowsGlob : null;
                asset ??= names.Where(IsWindowsArchive).OrderByDescending(Score).FirstOrDefault();
                if (asset is not null) { pattern ??= PatternFor(asset, tag); break; }
            }
            if (asset is not null) break;
        }
        if (asset is null || pattern is null) return (null, "no Windows .zip or .7z release");
        if (!IsSafePattern(pattern) || !(Glob(pattern)?.IsMatch(asset) ?? false)) return (null, $"can't make a pattern for {asset}");

        (string game, string name, string? fullTitle) = Titles(h, repo);
        string platform = Platforms.Name(h)!;
        string kind = h.Kind ?? "port";
        return (new CatalogPort
        {
            Id = Slug(repo.Split('/')[1]),
            Name = name,
            Game = game,
            ShortName = game,
            Aliases = h.Titles.Append(fullTitle ?? "").Where(t => t.Length > 0).Select(Tidy).Where(t => !t.Equals(game, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
            Platform = platform,
            Developer = repo.Split('/')[0],
            Kind = kind,
            Description = kind switch
            {
                "recomp" => $"{game} recompiled from the {Platforms.Short(platform)} version into a native PC game.",
                "decomp" => $"{game} running natively on PC, built from the decompiled {Platforms.Short(platform)} game.",
                _ => $"{game} ported to run natively on PC from the {Platforms.Short(platform)} version."
            },
            Requirement = "",
            Features = h.Features.Order(StringComparer.Ordinal).ToList(),
            RepositoryUrl = "https://github.com/" + repo,
            Stars = stars,
            License = asset,   // carried only to spot reuploads; Clean() empties it before writing
            Install = CatalogInstallKind.Download,
            Assets = [new CatalogAssetPattern { Pattern = pattern }]
        }, null);
    }

    /// <summary>Forks seen while checking, with the projects they came from.</summary>
    private static readonly Dictionary<string, List<string>> ForkOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops forks whose original is already listed or is being added: same port again.</summary>
    private static List<CatalogPort> DropForks(List<CatalogPort> ports, HashSet<string> listed, List<string> skipped)
    {
        var present = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        foreach (CatalogPort p in ports) present.Add(RepoOf(p.RepositoryUrl)!);
        var keep = new List<CatalogPort>();
        foreach (CatalogPort p in ports)
        {
            string repo = RepoOf(p.RepositoryUrl)!;
            if (ForkOf.TryGetValue(repo, out List<string>? ups) && ups.FirstOrDefault(present.Contains) is { } upstream)
                skipped.Add($"{repo}: duplicate: fork of {upstream}");
            else
                keep.Add(p);
        }
        return keep;
    }

    /// <summary>Same game on the same platform offering the same download file: a reupload. Keeps the most-starred one.</summary>
    private static List<CatalogPort> DropReuploads(List<CatalogPort> ports, List<string> skipped)
    {
        var keep = new List<CatalogPort>();
        foreach (var group in ports.GroupBy(p => (p.Platform, Game: Slug(p.Game), Asset: p.License.ToLowerInvariant())))
        {
            var ordered = group.OrderByDescending(p => p.Stars).ThenBy(p => p.RepositoryUrl, StringComparer.OrdinalIgnoreCase).ToList();
            keep.Add(ordered[0]);
            foreach (CatalogPort dup in ordered.Skip(1))
                skipped.Add($"{RepoOf(dup.RepositoryUrl)}: duplicate: same download as {RepoOf(ordered[0].RepositoryUrl)}");
        }
        return keep;
    }

    // RecompBox installs .zip and .7z downloads only; installers (.exe) and .rar are left for a person to look at.
    private static bool IsArchivePattern(string pattern) =>
        pattern.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || pattern.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Game and port names. Lists often write the game as "Game (Project)": the game is the part before the brackets
    /// (the whole title is kept as an alias), and the project name comes from the brackets when the list only gave a
    /// repository-style name. Repository-style names ("ghost-in-the-shell-recomp") get spaces.
    /// </summary>
    private static (string Game, string Name, string? FullTitle) Titles(Hint h, string repo)
    {
        string repoName = repo.Split('/')[1];
        string raw = Tidy(h.Game ?? h.Name ?? repoName);
        string? inBrackets = null, full = null;
        Match m = TrailingBracketsRegex().Match(raw);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            full = raw;
            inBrackets = m.Groups[2].Value.Trim();
            raw = m.Groups[1].Value.Trim();
        }
        string name = Tidy(h.Name ?? inBrackets ?? repoName);
        int junk = name.IndexOf('�');
        if (junk > 0) name = name[..junk].Trim();
        if (IsSlugLike(name) && inBrackets is not null && !IsSlugLike(inBrackets)) name = inBrackets;
        return (Humanize(raw), Humanize(name), full);
    }

    private static bool IsSlugLike(string s) => !s.Contains(' ') && s.IndexOfAny(['-', '_']) >= 0;

    private static string Humanize(string s) =>
        IsSlugLike(s) ? string.Join(' ', s.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..])) : s;

    [GeneratedRegex(@"^(.*?)\s*\(([^()]*)\)\s*$")]
    private static partial Regex TrailingBracketsRegex();

    private static bool IsWindowsArchive(string name) =>
        (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) &&
        WinRegex().IsMatch(name) && !NotWindowsRegex().IsMatch(name);

    private static int Score(string name) =>
        (X64Regex().IsMatch(name) ? 4 : 0) + (name.Contains("windows", StringComparison.OrdinalIgnoreCase) ? 2 : 0) -
        (DebugRegex().IsMatch(name) ? 5 : 0) - name.Length / 40;

    /// <summary>Turns an asset name into a pattern that also matches later releases: the tag and version numbers become *.</summary>
    internal static string PatternFor(string asset, string tag)
    {
        string p = asset;
        foreach (string v in new[] { tag, tag.TrimStart('v', 'V') }.Where(v => v.Length >= 2).Distinct())
            p = p.Replace(v, "*", StringComparison.OrdinalIgnoreCase);
        p = VersionRegex().Replace(p, "*");
        return StarsRegex().Replace(p, "*");
    }

    private static Regex? Glob(string pattern) => IsSafePattern(pattern)
        ? new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        : null;

    private static bool IsSafePattern(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && pattern.IndexOfAny(['/', '\\']) < 0 && !pattern.Contains("..", StringComparison.Ordinal) && pattern != "*";

    internal static string Slug(string s)
    {
        string slug = SlugRegex().Replace(s.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "port" : slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    private static string Tidy(string s) => s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Replace('–', '-').Trim();

    private static string? RepoOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && Program.IsRepo(u.AbsolutePath.Trim('/'))
            ? u.AbsolutePath.Trim('/') : null;

    internal static string? RepoFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match m = RepoUrlRegex().Match(text);
        if (m.Success) return m.Groups[1].Value.TrimEnd('.');
        return Program.IsRepo(text.Trim()) ? text.Trim() : null;
    }

    // ---------- Reading each outside list ----------

    private static IEnumerable<Hint> Read(string type, JsonElement root) => type switch
    {
        "recomplauncher" => Array(root, "apps").Select(a => new Hint
        {
            Repo = RepoFrom(Str(a, "repo")), Name = Str(a, "name"), Game = Str(a, "game"), Platform = Str(a, "origin"),
            WindowsGlob = a.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object ? Str(assets, "windows") : null,
            Features = Features(a.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetString() ?? "") : [])
        }),
        "recompendium" => Array(root, "entradas").Select(e => new Hint
        {
            Repo = RepoFrom(Str(e, "repo")), Name = Str(e, "proyecto"), Title = Str(e, "nombre"), Platform = Str(e, "consola"),
            Kind = Str(e, "metodo") switch { "recomp" => "recomp", "decomp-port" => "decomp", "source-port" or "native-layer" => "port", _ => null },
            Excluded = Str(e, "metodo") is "fan-remake" or "game-engine"
        }),
        "portcove" => Array(root, "listings").Select(l => new Hint
        {
            Repo = RepoFrom(Str(l, "project_url")), Name = Str(l, "name"), Game = Str(l, "original_game_title"),
            Platform = l.TryGetProperty("original_game", out var og) && og.ValueKind == JsonValueKind.Object ? Str(og, "platform") ?? FirstString(og, "platforms") : null
        }),
        "decompbuddy" => Array(root, "games").Select(g => new Hint
        {
            Repo = RepoFrom(Str(g, "repoUrl") ?? Str(g, "repo")), Name = Str(g, "project"), Game = Str(g, "title"), Platform = Str(g, "console")
        }),
        "portslauncher" => Array(root, "ports").Select(p => new Hint
        {
            Repo = RepoFrom(Str(p, "source")), Name = ParenName(Str(p, "name")), Title = BeforeParen(Str(p, "name")),
            Platform = p.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0 ? tags[0].GetString() : null
        }),
        "quiver-community" => Array(root, "apps").Select(a => new Hint
        {
            Repo = RepoFrom(Str(a, "repository")), Name = Str(a, "project"), Title = Str(a, "name"),
            Platform = a.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => Platforms.Map(x) is not null) : null,
            Kind = a.TryGetProperty("tags", out var k) && k.EnumerateArray().Any(x => x.GetString() == "recomp") ? "recomp" : null
        }),
        _ => throw new InvalidOperationException($"Unknown discover type '{type}'.")
    };

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
        : root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string? FirstString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => x is not null) : null;

    private static string? ParenName(string? s) => s is not null && s.LastIndexOf('(') is int i and > 0 && s.EndsWith(')') ? s[(i + 1)..^1].Trim() : null;
    private static string? BeforeParen(string? s) => s is not null && s.LastIndexOf('(') is int i and > 0 ? s[..i].Trim() : s?.TrimStart('-').Trim();

    private static HashSet<string> Features(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in tags)
            switch (t.ToLowerInvariant())
            {
                case "widescreen" or "ultrawide": set.Add(CatalogFeature.Widescreen); break;
                case "high fps" or "60 fps": set.Add(CatalogFeature.HighFps); break;
                case "hd textures": set.Add(CatalogFeature.HdTextures); break;
                case "mods": set.Add(CatalogFeature.Mods); break;
                case "online" or "netplay": set.Add(CatalogFeature.Online); break;
                case "gyro": set.Add(CatalogFeature.Gyro); break;
                case "randomizer": set.Add(CatalogFeature.Randomizer); break;
            }
        return set;
    }

    /// <summary>What the outside lists say about one repository, merged. The first list to give a value wins.</summary>
    private sealed record Hint
    {
        public string? Repo { get; init; }
        public string? Name { get; init; }
        public string? Game { get; init; }
        public string? Title { get; init; }
        public string? Platform { get; init; }
        public string? Kind { get; init; }
        public string? WindowsGlob { get; init; }
        public bool Excluded { get; init; }
        public HashSet<string> Features { get; init; } = [];
        public List<string> Platforms { get; init; } = [];

        public IEnumerable<string> Titles => new[] { Game, Title }.Where(t => !string.IsNullOrWhiteSpace(t))!;

        public Hint Merge(Hint other) => this with
        {
            Name = Name ?? other.Name,
            Game = Game ?? other.Game,
            Title = Title ?? other.Title,
            Platform = Platform ?? other.Platform,
            Kind = Kind ?? other.Kind,
            WindowsGlob = WindowsGlob ?? other.WindowsGlob,
            Excluded = Excluded || other.Excluded,
            Features = [.. Features, .. other.Features],
            Platforms = [.. Platforms, .. new[] { other.Platform }.Where(p => p is not null)!]
        };
    }

    private static class Platforms
    {
        // Every spelling the lists use, to LaunchBox's platform name. Anything not here (PC, DOS, Mac, phones) is skipped.
        private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
        {
            ["n64"] = "Nintendo 64", ["nintendo64"] = "Nintendo 64",
            ["snes"] = "Super Nintendo Entertainment System", ["supernintendo"] = "Super Nintendo Entertainment System", ["supernintendoentertainmentsystem"] = "Super Nintendo Entertainment System",
            ["nes"] = "Nintendo Entertainment System", ["nintendoentertainmentsystem"] = "Nintendo Entertainment System",
            ["gb"] = "Nintendo Game Boy", ["gameboy"] = "Nintendo Game Boy",
            ["gbc"] = "Nintendo Game Boy Color", ["gameboycolor"] = "Nintendo Game Boy Color",
            ["gba"] = "Nintendo Game Boy Advance", ["gameboyadvance"] = "Nintendo Game Boy Advance",
            ["ds"] = "Nintendo DS", ["nds"] = "Nintendo DS", ["nintendods"] = "Nintendo DS",
            ["3ds"] = "Nintendo 3DS", ["nintendo3ds"] = "Nintendo 3DS",
            ["gc"] = "Nintendo GameCube", ["gamecube"] = "Nintendo GameCube", ["nintendogamecube"] = "Nintendo GameCube",
            ["wii"] = "Nintendo Wii", ["nintendowii"] = "Nintendo Wii",
            ["wiiu"] = "Nintendo Wii U", ["nintendowiiu"] = "Nintendo Wii U",
            ["switch"] = "Nintendo Switch", ["nintendoswitch"] = "Nintendo Switch",
            ["switch2"] = "Nintendo Switch 2", ["nintendoswitch2"] = "Nintendo Switch 2",
            ["vb"] = "Nintendo Virtual Boy", ["virtualboy"] = "Nintendo Virtual Boy",
            ["ps1"] = "Sony Playstation", ["psx"] = "Sony Playstation", ["playstation"] = "Sony Playstation",
            ["ps2"] = "Sony Playstation 2", ["playstation2"] = "Sony Playstation 2",
            ["ps3"] = "Sony Playstation 3", ["playstation3"] = "Sony Playstation 3",
            ["ps4"] = "Sony Playstation 4", ["playstation4"] = "Sony Playstation 4",
            ["ps5"] = "Sony Playstation 5", ["playstation5"] = "Sony Playstation 5",
            ["psp"] = "Sony PSP", ["playstationportable"] = "Sony PSP",
            ["vita"] = "Sony Playstation Vita", ["psv"] = "Sony Playstation Vita", ["psvita"] = "Sony Playstation Vita",
            ["xbox"] = "Microsoft Xbox", ["x360"] = "Microsoft Xbox 360", ["360"] = "Microsoft Xbox 360", ["xbox360"] = "Microsoft Xbox 360",
            ["xone"] = "Microsoft Xbox One", ["xboxone"] = "Microsoft Xbox One",
            ["dc"] = "Sega Dreamcast", ["dreamcast"] = "Sega Dreamcast",
            ["saturn"] = "Sega Saturn",
            ["genesis"] = "Sega Genesis", ["megadrive"] = "Sega Genesis", ["md"] = "Sega Genesis", ["smd"] = "Sega Genesis",
            ["s32x"] = "Sega 32X", ["32x"] = "Sega 32X",
            ["segacd"] = "Sega CD", ["megacd"] = "Sega CD",
            ["sms"] = "Sega Master System", ["mastersystem"] = "Sega Master System",
            ["gg"] = "Sega Game Gear", ["gamegear"] = "Sega Game Gear",
            ["arcade"] = "Arcade",
            ["pce"] = "NEC TurboGrafx-16", ["pcengine"] = "NEC TurboGrafx-16",
            ["cdi"] = "Philips CD-i",
            ["3do"] = "3DO Interactive Multiplayer"
        };

        public static string? Map(string? value) =>
            value is null ? null : Names.GetValueOrDefault(KeyRegex().Replace(value.ToLowerInvariant(), ""));

        public static string? Name(Hint h) => Map(h.Platform) ?? h.Platforms.Select(Map).FirstOrDefault(n => n is not null);

        public static string Short(string platform) => platform
            .Replace("Super Nintendo Entertainment System", "SNES").Replace("Nintendo Entertainment System", "NES")
            .Replace("Sony Playstation", "PlayStation").Replace("Microsoft ", "").Replace("Nintendo ", "").Replace("Sega ", "");
    }

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex KeyRegex();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugRegex();
    [GeneratedRegex(@"github\.com/([A-Za-z0-9._-]+/[A-Za-z0-9._-]+)", RegexOptions.IgnoreCase)] private static partial Regex RepoUrlRegex();
    [GeneratedRegex(@"win(dows|32|64)?(?![a-z])|(?<![a-z])pc(?![a-z])", RegexOptions.IgnoreCase)] private static partial Regex WinRegex();
    [GeneratedRegex(@"arm64|aarch64|linux|mac|osx|darwin|android|ios|switch|source|src(?![a-z])|symbols|pdb|debuginfo", RegexOptions.IgnoreCase)] private static partial Regex NotWindowsRegex();
    [GeneratedRegex(@"x64|x86_64|amd64|win64", RegexOptions.IgnoreCase)] private static partial Regex X64Regex();
    [GeneratedRegex(@"debug", RegexOptions.IgnoreCase)] private static partial Regex DebugRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9])v?\d+(?:[._-]\d+)+(?:[-+.][0-9a-f]{6,40})?|(?<=[-_.])[0-9a-f]{7,40}(?=[-_.])", RegexOptions.IgnoreCase)] private static partial Regex VersionRegex();
    [GeneratedRegex(@"\*+")] private static partial Regex StarsRegex();
}
