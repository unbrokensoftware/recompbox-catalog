# RecompBox catalog

The list of native PC ports that RecompBox (the LaunchBox port browser) shows, installs and updates.

The app downloads [`catalog.json`](catalog.json) from this repo in the background and keeps a copy for offline use.
A job rebuilds it every day and whenever an entry changes on `main`.

## How it is built

[`feeds.json`](feeds.json) lists where entries come from. Feeds run top to bottom, and the first entry for an id or
GitHub repository wins.

| Type | What it reads |
|---|---|
| `local` | Entry files in a folder of this repo (`sources/`) |
| `url` | An https JSON file in the same entry format (one entry or an array) |
| `retcomm` | A RetComM-style catalog repo, mapped to our format, only for the platforms listed |
| `github-topics` | Searches GitHub topics and writes [`candidates/`](candidates) for review. Never published |

To change where we pull from, edit `feeds.json`: switch a feed with `"enabled": false`, or copy a block to add one.
The next run uses it.

For every entry the generator reads the GitHub repository (stars, license, creation date) and its latest releases
that have a Windows download matching the entry's asset pattern. Entries that fail a check are left out with a
warning in the job log. If GitHub can't be reached, nothing is written, so a partial catalog is never published.

## Upkeep

Every run also checks that each listed project still works, and records what it found in
[`maintenance.md`](maintenance.md):

- **Moved:** GitHub redirects renamed or transferred repositories. The run follows the redirect and rewrites the
  address in our source files, so the change is saved with the catalog.
- **Failing:** the repository is gone, or no release has a Windows download matching the entry any more. The port
  stays listed as last seen, in case it's temporary. [`health.json`](health.json) records when it started failing.
- **Removed:** failing for `removeAfterDays` (7, in `feeds.json`). It's left out of the catalog, but its entry stays,
  so it comes back on its own once it works again.
- Ports that are simply quiet (no new release in a long time) are never removed.

## Finding new ports

Every Monday a harvest job (`--harvest`) reads the outside lists under `discover` in `feeds.json`. They are only
used to find GitHub repositories. Each one has to pass our own checks before it's added to `sources/discovered/` as
our own entry:

- It's a console game, on a platform we can match in LaunchBox (PC, DOS, Mac and phone projects are skipped).
- Its GitHub repository has a release with a Windows .zip or .7z download.
- It isn't one we already list. Duplicates are caught by repository (any case, after following moves), forks of a
  listed project, and reuploads (same game, same platform, same download file; the most-starred one is kept).
  Different projects for the same game are kept. They're alternatives.

Descriptions are our own short text, not copied from the lists. What the harvest found but couldn't use is in
`candidates/harvest-skipped.md`. Once added, an entry is ours: edit it freely; the harvest never changes it again.

Lists we read: [RecompLauncher](https://recomplauncher.com), [Recompendium](https://recompendium.com)
(data CC BY-SA 4.0), [Portcove](https://github.com/boburning/portcove) (Apache-2.0),
[Decomp Buddy](https://tanookistudios.com/decomp-buddy/), [Ports Launcher](https://github.com/Nyaldee/Ports-Launcher)
and the [Quiver community catalog](https://github.com/tgeorgiadis/quiver-community-app-catalog). Thanks to all of them.

## Adding a port

Add a JSON file under `sources/community/` and open a pull request. A check runs the generator on it.

```json
{
  "id": "starship",
  "name": "Starship",
  "game": "Star Fox 64",
  "platform": "Nintendo 64",
  "developer": "HarbourMasters",
  "kind": "decomp",
  "description": "Star Fox 64 on the libultraship engine with enhancements and custom assets.",
  "requirement": "US 1.1 version",
  "features": ["widescreen", "high-fps", "mods"],
  "repositoryUrl": "https://github.com/HarbourMasters/Starship",
  "install": "download",
  "assets": [{ "pattern": "Starship-*-Windows.zip" }]
}
```

- `id`: lowercase letters, digits and dashes.
- `platform`: the LaunchBox platform name, so the app can match the game in your library.
- `game` and `aliases`: titles used to match library games.
- `assets`: a file name pattern for the Windows release download (`*` and `?`), .zip or .7z.
- `features`: any of `widescreen`, `high-fps`, `hd-textures`, `mods`, `online`, `gyro`, `randomizer`.
- Optional: `roms` (`label`, `sha1`, `sizeBytes` of the byte-swapped-to-big-endian ROM), `romFile`, `executable`,
  `saves`, `modsFolder`, `mods`, `portableFile`, `colors` (three `#rrggbb` values for the card placeholder).
- `portableFile`: an empty file created in the install folder after every install, for ports that keep their
  settings, saves and mods next to the exe when it's there (Zelda64Recompiled uses `portable.txt`).
- `mods`: each has `id`, `name`, `adds`, `category`, `sizeBytes`, `url`, `sha256`, and optionally `defaultOn`,
  `required`, `features` and `requires` (ids of other mods of the same port that must be installed and on with
  it). A mod in the `library` category isn't shown; it's installed only when a chosen mod requires it. A URL with
  no file name, like Thunderstore's `https://thunderstore.io/package/download/{team}/{mod}/{version}/`, is
  downloaded as a .zip, and the manifest, icon and readme at the top of a Thunderstore package are left out.

We list ports and mods that enhance the original game. Total conversions and romhack campaigns are not listed.
`picks.json` holds the LaunchBox picks.

## Running it locally

```sh
GITHUB_TOKEN=... dotnet run --project generator -c Release
```

Options: `--out <file>`, `--strict` (fail if any of our own entries is rejected), `--no-candidates`.

`generator/Catalog.cs` is the schema shared with the app. Keep it the same as `Models/Catalog.cs` in the plugin.
