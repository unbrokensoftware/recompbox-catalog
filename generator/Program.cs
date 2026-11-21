using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SM64EnhancedWindowsBuilder.Models;

namespace CatalogGenerator;

internal static partial class Program
{
    private const int MaxReleases = 5;
    private static int _strictRejections;

    internal static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static async Task<int> Main(string[] args)
    {
        string? root = null;
        string? output = null;
        bool strict = false, candidates = true, harvest = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = Path.GetFullPath(args[++i]); break;
                case "--out": output = Path.GetFullPath(args[++i]); break;
                case "--strict": strict = true; break;
                case "--no-candidates": candidates = false; break;
                case "--harvest": harvest = true; break;
                default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 2;
            }
        }
        root = Path.TrimEndingDirectorySeparator(root ?? FindRoot()) + Path.DirectorySeparatorChar;
        output ??= Path.Combine(root, "catalog.json");

        try { return harvest ? await Harvest.RunAsync(root) : await RunAsync(root, output, strict, candidates); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine($"Failed: {ex.Message} Nothing written.");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string root, string output, bool strict, bool candidates)
    {
        FeedFile feeds = JsonSerializer.Deserialize<FeedFile>(File.ReadAllText(Path.Combine(root, "feeds.json")), ReadOptions) ?? new();
        var picks = new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(root, "picks.json")), ReadOptions) ?? [], StringComparer.Ordinal);

        (HttpClient api, HttpClient web, GitHub github) = CreateClients(root);
        using var apiScope = api;
        using var webScope = web;

        // Feeds are read in file order; the first entry for an id or repository wins, so our own entries go first.
        var entries = new List<SourceEntry>();
        foreach (FeedConfig feed in feeds.Feeds.Where(f => f.Enabled && f.Type != FeedType.GitHubTopics))
        {
            List<SourceEntry> loaded = await Feeds.LoadAsync(feed, root, web);
            Console.WriteLine($"Feed {feed.Id}: {loaded.Count} entries.");
            entries.AddRange(loaded);
        }

        // Upkeep state: the last published catalog (last good copy of each port) and which ports are failing since when.
        Dictionary<string, CatalogPort> lastGood = LoadPrevious(output);
        string healthFile = Path.Combine(root, "health.json");
        var health = File.Exists(healthFile)
            ? JsonSerializer.Deserialize<Dictionary<string, PortHealth>>(File.ReadAllText(healthFile), ReadOptions) ?? []
            : new Dictionary<string, PortHealth>();
        var stillFailing = new Dictionary<string, PortHealth>(StringComparer.Ordinal);
        var report = new Report();
        DateTimeOffset today = DateTimeOffset.UtcNow;

        var ports = new List<CatalogPort>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceEntry source in entries)
        {
            CatalogPort entry = source.Entry;
            string label = string.IsNullOrWhiteSpace(entry.Id) ? source.Origin : entry.Id;
            string? repo = GitHubRepo(entry.RepositoryUrl);
            if (!source.Strict && repo is not null && repos.Contains(repo))
                continue;
            string? reason = ValidateSource(entry) ?? (ids.Contains(entry.Id) ? "duplicate id" : null);
            if (reason is not null)
            {
                // Our own mistake in the entry, not the project breaking: no grace period.
                Reject($"{label} ({source.Origin})", source.Feed, reason, source.Strict);
                report.Rejected.Add($"`{label}` ({source.Origin}): {reason}");
                continue;
            }

            (CatalogPort? port, reason, string? movedTo) = await EnrichAsync(github, entry with { Pick = picks.Contains(entry.Id) });
            reason ??= ValidateOutput(port!);
            if (movedTo is not null && repo is not null)
            {
                report.Moved.Add($"`{entry.Id}`: {repo} → {movedTo}");
                if (source.Strict) FixSource(root, source.Origin, entry.RepositoryUrl, "https://github.com/" + movedTo);
                repo = movedTo;
            }

            if (reason is not null)
            {
                // The project stopped working (gone, or no Windows download matches any more). Keep the last good copy
                // for a while in case it's temporary; drop it once it has failed for RemoveAfterDays.
                PortHealth h = health.TryGetValue(entry.Id, out PortHealth? old) ? old with { LastError = reason } : new PortHealth { FirstFailed = today, LastError = reason };
                stillFailing[entry.Id] = h;
                int days = (int)(today - h.FirstFailed).TotalDays;
                if (days < feeds.RemoveAfterDays && lastGood.TryGetValue(entry.Id, out CatalogPort? kept))
                {
                    report.Failing.Add($"`{entry.Id}` ({source.Origin}): {reason}. Failing {days} days; still listed, removed after {feeds.RemoveAfterDays}.");
                    Warn($"Keeping last good {entry.Id} from {source.Feed}: {reason}");
                    port = kept with { Pick = picks.Contains(entry.Id) };
                }
                else
                {
                    if (lastGood.ContainsKey(entry.Id) || health.ContainsKey(entry.Id))
                        report.Removed.Add($"`{entry.Id}` ({source.Origin}): {reason}. Failing since {h.FirstFailed:yyyy-MM-dd}; comes back on its own once it works.");
                    else
                        report.Rejected.Add($"`{label}` ({source.Origin}): {reason}");
                    Reject($"{label} ({source.Origin})", source.Feed, reason, source.Strict && !lastGood.ContainsKey(entry.Id) && !health.ContainsKey(entry.Id));
                    continue;
                }
            }
            // Same repository (after following moves) with the same download is the same port listed twice.
            string dupKey = (repo ?? port!.Id) + "|" + string.Join(",", port!.Assets.Select(a => a.Pattern.ToLowerInvariant()).Order());
            if (seen.TryGetValue(dupKey, out string? firstId))
            {
                Reject($"{label} ({source.Origin})", source.Feed, $"duplicate of {firstId}", source.Strict);
                report.Rejected.Add($"`{label}` ({source.Origin}): duplicate of `{firstId}`");
                continue;
            }
            seen[dupKey] = port.Id;
            ids.Add(port.Id);
            if (repo is not null) repos.Add(repo);
            ports.Add(port);
        }

        foreach (string pick in picks.Where(p => !ids.Contains(p)).Order(StringComparer.Ordinal))
            Warn($"Pick {pick} has no catalog entry.");
        if (ports.Count == 0)
        {
            Console.Error.WriteLine("No valid entries; nothing written.");
            return 1;
        }
        if (strict && _strictRejections > 0)
        {
            Console.Error.WriteLine($"{_strictRejections} of our own entries were rejected; nothing written.");
            return 1;
        }

        ports.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var catalog = new Catalog
        {
            SchemaVersion = 1,
            Generated = ports.Max(p => p.Updated > p.Added ? p.Updated : p.Added),
            Ports = ports
        };
        Write(output, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(catalog, WriteOptions) + "\n"));
        Console.WriteLine($"Wrote {ports.Count} ports, rejected {_rejections}.");
        if (Path.GetDirectoryName(output) == Path.TrimEndingDirectorySeparator(root))
        {
            // Upkeep state only follows the published catalog, not trial runs written elsewhere.
            Write(healthFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SortedDictionary<string, PortHealth>(stillFailing, StringComparer.Ordinal), WriteOptions) + "\n"));
            Write(Path.Combine(root, "maintenance.md"), Encoding.UTF8.GetBytes(report.ToMarkdown(ports.Count)));
        }

        if (candidates)
        {
            foreach (FeedConfig feed in feeds.Feeds.Where(f => f.Enabled && f.Type == FeedType.GitHubTopics))
            {
                // Candidates are a report, not the catalog: a failed search never fails the run.
                try { await Candidates.RunAsync(feed, root, github, repos); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException)
                { Warn($"Feed {feed.Id}: skipped ({ex.Message})"); }
            }
        }
        return 0;
    }

    private static int _rejections;

    internal static void Reject(string label, string feed, string reason, bool strict)
    {
        _rejections++;
        if (strict) _strictRejections++;
        Warn($"Rejected {label} from {feed}: {reason}");
    }

    private static string? ValidateSource(CatalogPort p)
    {
        if (!IsId(p.Id)) return "id must be lowercase letters, digits and dashes";
        if (string.IsNullOrWhiteSpace(p.Name)) return "missing name";
        if (string.IsNullOrWhiteSpace(p.Platform)) return "missing platform";
        if (p.Install is not (CatalogInstallKind.Download or CatalogInstallKind.Build)) return $"unknown install kind '{p.Install}'";
        if (!IsHttpsOrEmpty(p.RepositoryUrl)) return "repository URL must be https";
        if (p.Install == CatalogInstallKind.Download)
        {
            if (string.IsNullOrEmpty(p.RepositoryUrl)) return "download entries need a repository";
            if (GitHubRepo(p.RepositoryUrl) is null) return "download entries must be GitHub repositories";
            if ((p.Assets ?? []).Count == 0) return "download entries need an asset pattern";
        }
        else if (!IsId(p.Recipe)) return "build entries need a recipe";
        return null;
    }

    private static string? ValidateOutput(CatalogPort p)
    {
        foreach (var a in p.Assets)
            if (a is null || !IsSafePattern(a.Pattern)) return $"unsafe asset pattern '{a?.Pattern}'";
        foreach (string c in p.Colors)
            if (c is null || !ColorRegex().IsMatch(c)) return $"bad color '{c}'";
        if (p.Aliases.Any(string.IsNullOrWhiteSpace) || p.Features.Any(string.IsNullOrWhiteSpace)) return "blank alias or feature";
        foreach (var r in p.Releases)
            if (r is null || string.IsNullOrWhiteSpace(r.Version) || r.SizeBytes < 0) return "release without a version";
        foreach (var r in p.Roms)
            if (r is null || !IsHex(r.Sha1, 40) || r.SizeBytes < 0) return $"ROM '{r?.Label}' needs a 40-hex SHA-1";
        var modIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in p.Mods)
        {
            if (m is null || !IsId(m.Id)) return "mod id must be lowercase letters, digits and dashes";
            if (!modIds.Add(m.Id!)) return $"duplicate mod id '{m.Id}'";
            if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Category)) return $"mod '{m.Id}' needs a name and category";
            if (m.Category.Equals(CatalogModCategory.Conversion, StringComparison.OrdinalIgnoreCase)) return $"mod '{m.Id}' is a conversion";
            if (m.SizeBytes < 0 || !IsHttpsOrEmpty(m.Url)) return $"mod '{m.Id}' URL must be https";
            if (!string.IsNullOrEmpty(m.Sha256) && !IsHex(m.Sha256, 64)) return $"mod '{m.Id}' needs a 64-hex SHA-256";
            if (m.Features.Any(string.IsNullOrWhiteSpace)) return $"mod '{m.Id}' has a blank feature";
        }
        if (p.Mods.Count > 0 && string.IsNullOrWhiteSpace(p.ModsFolder)) return "mods need a modsFolder";
        foreach (var m in p.Mods)
            foreach (string need in m.Requires)
                if (need == m.Id || !modIds.Contains(need)) return $"mod '{m.Id}' requires '{need}', which isn't a mod of this port";
        if (p.Mods.Any(m => m.IsLibrary && m.Required)) return "a library is installed only when a mod requires it, so it can't be required";
        if (!string.IsNullOrEmpty(p.PortableFile) && (p.PortableFile.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(p.PortableFile) || p.PortableFile.Contains(':')))
            return "portableFile must be a file inside the install folder";
        return null;
    }

    private static Dictionary<string, CatalogPort> LoadPrevious(string output)
    {
        try
        {
            if (File.Exists(output) && JsonSerializer.Deserialize<Catalog>(File.ReadAllText(output), ReadOptions) is { } previous)
                return previous.Ports.Where(p => p is not null && IsId(p.Id)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (JsonException) { }
        return new(StringComparer.Ordinal);
    }

    /// <summary>Points a source file at a repository's new address after GitHub reports it moved.</summary>
    private static void FixSource(string root, string origin, string oldUrl, string newUrl)
    {
        string file = Path.GetFullPath(Path.Combine(root, origin));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return;
        string text = File.ReadAllText(file);
        string quoted = JsonSerializer.Serialize(oldUrl);
        if (!text.Contains(quoted, StringComparison.Ordinal)) return;
        Write(file, Encoding.UTF8.GetBytes(text.Replace(quoted, JsonSerializer.Serialize(newUrl), StringComparison.Ordinal)));
    }

    private sealed class Report
    {
        public List<string> Moved { get; } = [];
        public List<string> Failing { get; } = [];
        public List<string> Removed { get; } = [];
        public List<string> Rejected { get; } = [];

        public string ToMarkdown(int listed)
        {
            var sb = new StringBuilder("# Catalog upkeep\n\n");
            sb.Append($"Written by the catalog job. {listed} ports listed.\n");
            Section(sb, "Moved", "GitHub reports these repositories moved. The catalog and our source files now use the new address.", Moved);
            Section(sb, "Failing", "Stopped working recently. Still listed as last seen, in case it's temporary.", Failing);
            Section(sb, "Removed", "Stopped working for too long and left out of the catalog. Their entries stay, so they return once they work again.", Removed);
            Section(sb, "Rejected", "Entries that never made it in. Fix or delete them.", Rejected);
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, string about, List<string> items)
        {
            sb.Append($"\n## {title} ({items.Count})\n\n");
            if (items.Count == 0) { sb.Append("None.\n"); return; }
            sb.Append(about).Append("\n\n");
            foreach (string item in items.Order(StringComparer.Ordinal)) sb.Append("- ").Append(item.Replace("\n", " ")).Append('\n');
        }
    }

    private static async Task<(CatalogPort? Port, string? Reason, string? MovedTo)> EnrichAsync(GitHub github, CatalogPort entry)
    {
        (CatalogPort? port, string? reason) = await EnrichCoreAsync(github, entry);
        string? from = GitHubRepo(entry.RepositoryUrl);
        string? to = GitHubRepo(port?.RepositoryUrl);
        return (port, reason, from is not null && to is not null && !from.Equals(to, StringComparison.OrdinalIgnoreCase) ? to : null);
    }

    private static async Task<(CatalogPort? Port, string? Reason)> EnrichCoreAsync(GitHub github, CatalogPort entry)
    {
        var port = entry with
        {
            ShortName = string.IsNullOrWhiteSpace(entry.ShortName) ? entry.Name : entry.ShortName,
            Aliases = entry.Aliases ?? [],
            Features = entry.Features ?? [],
            Colors = entry.Colors ?? [],
            Releases = entry.Releases ?? [],
            Assets = entry.Assets ?? [],
            Roms = entry.Roms ?? [],
            Mods = (entry.Mods ?? []).Select(m => m is null ? m! : m with { Features = m.Features ?? [], Requires = m.Requires ?? [] }).ToList()
        };
        if (GitHubRepo(port.RepositoryUrl) is not { } repo) return (port, null);

        JsonElement? info = await github.GetAsync($"repos/{repo}");
        if (info is not { } r) return (null, $"GitHub repository {repo} not found");
        // A renamed or transferred repository redirects; carry on at its new address.
        if (r.GetProperty("full_name").GetString() is { } fullName && IsRepo(fullName) && !fullName.Equals(repo, StringComparison.OrdinalIgnoreCase))
        {
            repo = fullName;
            port = port with { RepositoryUrl = "https://github.com/" + fullName };
        }
        string license = r.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object &&
                         lic.TryGetProperty("spdx_id", out var spdx) && spdx.GetString() is { } id && id != "NOASSERTION" ? id : "";
        port = port with
        {
            Stars = r.GetProperty("stargazers_count").GetInt32(),
            License = license.Length > 0 ? license : port.License,
            Added = r.GetProperty("created_at").GetDateTimeOffset()
        };

        if (port.Install == CatalogInstallKind.Build)
        {
            var pinned = port.Releases.OrderByDescending(x => x.Published).ThenBy(x => x.Version, StringComparer.Ordinal).ToList();
            return (port with { Releases = pinned, Updated = pinned.Count > 0 ? pinned[0].Published : port.Added }, null);
        }

        JsonElement? list = await github.GetAsync($"repos/{repo}/releases?per_page=50");
        var stable = new List<CatalogRelease>();
        var pre = new List<CatalogRelease>();
        var matchers = port.Assets.Where(a => a is not null && IsSafePattern(a.Pattern) && a.Os == "windows").Select(a => Glob(a.Pattern)).ToList();
        if (matchers.Count == 0) return (null, "no safe Windows asset pattern");
        foreach (JsonElement rel in list?.EnumerateArray() ?? Enumerable.Empty<JsonElement>())
        {
            if (rel.GetProperty("draft").GetBoolean()) continue;
            long? size = null;
            foreach (JsonElement asset in rel.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (matchers.Any(m => m.IsMatch(name))) { size = asset.GetProperty("size").GetInt64(); break; }
            }
            if (size is null) continue;
            string version = rel.GetProperty("tag_name").GetString() ?? "";
            JsonElement published = rel.GetProperty("published_at");
            if (version.Length == 0 || published.ValueKind != JsonValueKind.String) continue;
            var release = new CatalogRelease { Version = version, Published = published.GetDateTimeOffset(), SizeBytes = size.Value };
            (rel.GetProperty("prerelease").GetBoolean() ? pre : stable).Add(release);
        }
        var releases = (stable.Count > 0 ? stable : pre)
            .OrderByDescending(x => x.Published).ThenBy(x => x.Version, StringComparer.Ordinal).Take(MaxReleases).ToList();
        if (releases.Count == 0) return (null, $"no release has an asset matching {string.Join(", ", port.Assets.Select(a => a.Pattern))}");
        return (port with { Releases = releases, Updated = releases[0].Published }, null);
    }

    internal static bool IsId(string? id) => IdRegex().IsMatch(id ?? "");

    internal static bool RepoPart(string? part) => RepoPartRegex().IsMatch(part ?? "") && part != "." && part != "..";

    internal static bool IsRepo(string? repo) => repo?.Split('/') is [var owner, var name] && RepoPart(owner) && RepoPart(name);

    private static string? GitHubRepo(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        string repo = uri.AbsolutePath.Trim('/');
        return IsRepo(repo) ? repo : null;
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsHttpsOrEmpty(string? url) =>
        string.IsNullOrEmpty(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSafePattern(string? pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && pattern.IndexOfAny(['/', '\\']) < 0 && !pattern.Contains("..", StringComparison.Ordinal);

    private static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);

    internal static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, true);
        Console.WriteLine($"Updated {path}");
    }

    private static void Warn(string message) =>
        Console.Error.WriteLine(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "::warning::" + message : message);

    private static string FindRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "feeds.json"))) return dir.FullName;
        throw new InvalidOperationException("Cannot find feeds.json; run from the catalog repo or pass --root.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex RepoPartRegex();

    internal static (HttpClient Api, HttpClient Web, GitHub GitHub) CreateClients(string root)
    {
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token)) Warn("No GITHUB_TOKEN set; GitHub allows only 60 unauthenticated requests an hour.");

        // The token only ever goes to api.github.com. Feeds read files through a separate client without it.
        // Redirects (moved repositories) are followed by GitHub.FetchAsync: HttpClient would drop the token on them, and
        // those requests would then count against the 60-an-hour unauthenticated limit.
        var api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        api.DefaultRequestHeaders.UserAgent.ParseAdd("RecompBox-CatalogGenerator/1.0");
        api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        api.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token)) api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var web = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        web.DefaultRequestHeaders.UserAgent.ParseAdd("RecompBox-CatalogGenerator/1.0");
        return (api, web, new GitHub(api, Path.Combine(root, ".cache", "github")));
    }

    /// <summary>
    /// GitHub REST reads with a disk cache of ETags: unchanged answers come back as 304, which don't count against the
    /// rate limit, so checking every port daily stays cheap. When the limit is used up it waits for the reset.
    /// </summary>
    internal sealed class GitHub(HttpClient http, string cacheDir)
    {
        // One request per cache file at a time: two lookups of the same path (a moved repo found under both names)
        // would otherwise read and write the same file together.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> busy = new();

        public async Task<JsonElement?> GetAsync(string path)
        {
            string key = Path.Combine(cacheDir, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path))));
            SemaphoreSlim gate = busy.GetOrAdd(key, _ => new SemaphoreSlim(1));
            await gate.WaitAsync();
            try { return await FetchAsync(path, key); }
            finally { gate.Release(); }
        }

        private async Task<JsonElement?> FetchAsync(string path, string key)
        {
            var url = new Uri("https://api.github.com/" + path);
            for (int attempt = 1, hops = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                string? etag = File.Exists(key + ".etag") && File.Exists(key + ".json") ? File.ReadAllText(key + ".etag") : null;
                if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
                using var response = await http.SendAsync(request);
                if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } moved)
                {
                    var next = moved.IsAbsoluteUri ? moved : new Uri(url, moved);
                    if (next.Scheme != "https" || next.Host != "api.github.com" || ++hops > 5)
                        throw new HttpRequestException($"GitHub {path} redirected to {next.Host}.");
                    url = next;
                    attempt = 0;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotModified && etag is not null)
                {
                    using var cached = JsonDocument.Parse(File.ReadAllText(key + ".json"));
                    return cached.RootElement.Clone();
                }
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (response.Headers.ETag is { } fresh)
                    {
                        Directory.CreateDirectory(cacheDir);
                        File.WriteAllText(key + ".json", body);
                        File.WriteAllText(key + ".etag", fresh.ToString());
                    }
                    return doc.RootElement.Clone();
                }
                if (RateLimitWait(response) is { } wait)
                {
                    Warn($"GitHub rate limit reached; waiting {wait.TotalMinutes:0} minutes.");
                    await Task.Delay(wait);
                    attempt = 0;
                    continue;
                }
                if (attempt >= 3 || (int)response.StatusCode < 500)
                    throw new HttpRequestException($"GitHub {path} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }

        private static TimeSpan? RateLimitWait(HttpResponseMessage response)
        {
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;
            if (response.Headers.RetryAfter?.Delta is { } delta) return delta + TimeSpan.FromSeconds(1);
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
                response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out long epoch))
            {
                TimeSpan wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                return wait < TimeSpan.Zero ? TimeSpan.FromSeconds(5) : wait > TimeSpan.FromMinutes(65) ? TimeSpan.FromMinutes(65) : wait;
            }
            return response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : null;
        }
    }
}
