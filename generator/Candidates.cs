using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatalogGenerator;

/// <summary>
/// github-topics feed: finds repositories tagged with the configured topics that are not in the catalog yet
/// and have a Windows download, and writes them to a report for a person to turn into entries. Nothing here
/// is published.
/// </summary>
internal static partial class Candidates
{
    public static async Task RunAsync(FeedConfig feed, string root, Program.GitHub github, HashSet<string> known)
    {
        string output = Path.GetFullPath(Path.Combine(root, feed.Output.Length > 0 ? feed.Output : $"candidates/{feed.Id}.md"));
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Feed {feed.Id}: output must be inside the repo.");

        var found = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        foreach (string topic in feed.Topics.Where(t => TopicRegex().IsMatch(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonElement? result = await github.GetAsync(
                $"search/repositories?q=topic:{topic}+fork:false+archived:false+stars:>={Math.Max(0, feed.MinStars)}&sort=stars&order=desc&per_page={Math.Clamp(feed.MaxPerTopic, 1, 100)}");
            foreach (JsonElement repo in result?.GetProperty("items").EnumerateArray() ?? Enumerable.Empty<JsonElement>())
            {
                string fullName = repo.GetProperty("full_name").GetString() ?? "";
                if (!Program.IsRepo(fullName) || known.Contains(fullName)) continue;
                if (found.TryGetValue(fullName, out Row? row)) { row.Topics.Add(topic); continue; }

                JsonElement? latest = await github.GetAsync($"repos/{fullName}/releases/latest");
                string? asset = latest is { } rel ? WindowsAsset(rel) : null;
                if (asset is null) continue;
                found[fullName] = new Row(
                    fullName,
                    repo.GetProperty("stargazers_count").GetInt32(),
                    repo.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "",
                    latest!.Value.GetProperty("tag_name").GetString() ?? "",
                    asset,
                    [topic]);
            }
        }

        var sb = new StringBuilder();
        sb.Append("# Candidates from GitHub topics\n\n");
        sb.Append("Repositories tagged ").Append(string.Join(", ", feed.Topics.Select(t => $"`{t}`")))
          .Append($" with at least {feed.MinStars} stars and a Windows download, not in the catalog yet.\n");
        sb.Append("Written by the catalog job. To list one, add an entry under `sources/` (see README).\n\n");
        if (found.Count == 0) sb.Append("None right now.\n");
        else
        {
            sb.Append("| Repository | Stars | Latest | Windows asset | Topics | About |\n|---|---|---|---|---|---|\n");
            foreach (Row r in found.Values.OrderByDescending(r => r.Stars).ThenBy(r => r.Repo, StringComparer.OrdinalIgnoreCase))
                sb.Append($"| [{r.Repo}](https://github.com/{r.Repo}) | {r.Stars} | {Cell(r.Tag)} | `{Cell(r.Asset)}` | {string.Join(", ", r.Topics.Order())} | {Cell(r.About)} |\n");
        }
        Program.Write(output, Encoding.UTF8.GetBytes(sb.ToString()));
        Console.WriteLine($"Feed {feed.Id}: {found.Count} candidates.");
    }

    private static string? WindowsAsset(JsonElement release)
    {
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (WindowsRegex().IsMatch(name) && !ArmRegex().IsMatch(name) &&
                (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return null;
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private sealed record Row(string Repo, int Stars, string About, string Tag, string Asset, SortedSet<string> Topics);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex TopicRegex();

    [GeneratedRegex("win(dows|32|64)?", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsRegex();

    [GeneratedRegex("arm64|aarch64", RegexOptions.IgnoreCase)]
    private static partial Regex ArmRegex();
}
