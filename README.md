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
  `saves`, `modsFolder`, `mods`, `colors` (three `#rrggbb` values for the card placeholder).

We list ports and mods that enhance the original game. Total conversions and romhack campaigns are not listed.
`picks.json` holds the LaunchBox picks.

## Running it locally

```sh
GITHUB_TOKEN=... dotnet run --project generator -c Release
```

Options: `--out <file>`, `--strict` (fail if any of our own entries is rejected), `--no-candidates`.

`generator/Catalog.cs` is the schema shared with the app. Keep it the same as `Models/Catalog.cs` in the plugin.
