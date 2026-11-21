<h1 align="center">Jellyfin Merge Versions Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org">Jellyfin Project</a></h3>

<p align="center">
Jellyfin Merge Versions plugin is a plugin that automatically groups every repeated movie and episode.

</p>

## About this fork

This fork adds a **preferred default version**, which upstream cannot offer since it
switched to Jellyfin's own merge ([#72](https://github.com/danieladov/jellyfin-plugin-mergeversions/issues/72),
[#97](https://github.com/danieladov/jellyfin-plugin-mergeversions/pull/97)). Jellyfin always makes the widest
file the default version of a merged item, so 4K copies play by default even on clients that
have to transcode them. Jellyfin issue [#17664](https://github.com/jellyfin/jellyfin/issues/17664)
asks for a way to choose.

- **Preferred folders** (plugin settings), one per line, highest priority first. A merged item
  plays its version from the first of these folders by default; the other versions stay in its
  version menu. Items with no version in these folders keep Jellyfin's choice.
- The merge tasks apply the preference after merging. **Apply Preferred Default Versions**
  (scheduled task, or the button in the settings) applies it to everything already merged.
- Versions whose default version was deleted from disk are unlinked, so they stand alone and
  can be merged again.

How it sets the default: it splits the group, links the other versions to the preferred one,
and calls Jellyfin's own merge, which keeps a version that already has alternates as the
default. Jellyfin still does the rest, including moving playlist and collection entries to the
new default. It checks the result and logs a warning if Jellyfin chose another default.

A merged item shows the watched state of its default version. Check that history on the old
default versions is also on the new ones before applying the preference.

This fork has its own plugin ID and name ("Merge Versions (Preferred Default)"), so it is
never replaced by an update of the upstream plugin. Remove the upstream plugin before
installing it: both register the same API routes. It reads the upstream plugin's settings file.

## Install Process


## From Repository
1. In jellyfin, go to dashboard -> plugins -> Repositories -> add and paste this link https://raw.githubusercontent.com/danieladov/JellyfinPluginManifest/master/manifest.json
2. Go to Catalog and search for the plugin you want to install
3. Click on it and install
4. Restart Jellyfin


## From .zip file
1. Download the .zip file from release page
2. Extract it and place the .dll file in a folder called ```plugins/Merge Versions``` under  the program data directory or inside the portable install directory
3. Restart Jellyfin

## User Guide
1. To merge your movies or episodes you can do it from Schedule task or directly from the configuration of the plugin.
2. Spliting is only avaible through the configuration

Merge and split use the installed Jellyfin server's native video-version actions. No URL or API key configuration is needed. Manual operations require administrator permissions.

This does not migrate or repair local-version groups created by earlier plugin releases. Split follows Jellyfin's native behavior for linked alternate versions; it does not detach local alternate versions.

Cancellation stops the scan between groups, after the current native operation finishes.



## Build Process
1. Clone or download this repository
2. Ensure you have .NET Core SDK setup and installed
3. Build plugin with following command.
```sh
dotnet publish Jellyfin.Plugin.MergeVersions/Jellyfin.Plugin.MergeVersions.csproj --configuration Release --output bin
```
4. Place the resulting .dll file in a folder called ```plugins/Merge versions``` under  the program data directory or inside the portable install directory

## Tests

```sh
dotnet test Jellyfin.Plugin.MergeVersions.sln --configuration Release
```

The tests cover controller discovery/dispatch using a test double, DI scope lifetime, error handling, authorization requirements, asynchronous completion and cancellation. They do not replace a merge/split smoke test against a Jellyfin test library.

The adapter discovers `Jellyfin.Api.Controllers.VideosController` through MVC and resolves it from Jellyfin's service container. If Jellyfin changes the action signatures, operations fail with a compatibility error instead of falling back to manual relationship changes.


