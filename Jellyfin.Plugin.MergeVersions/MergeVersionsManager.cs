using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
        private readonly IFileSystem _fileSystem;
        private readonly IVideoVersions _videoVersions;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem,
            IVideoVersions videoVersions
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _videoVersions = videoVersions;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task MergeMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1 &&
                group.Any(movie => movie.PrimaryVersionId == null &&
                                    !movie.LinkedAlternateVersions.Any()))
                .ToList();

            var current = 0;
            foreach (var movies in duplicateMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = movies.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(movies.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateMovies.Count * 100);
            }

            await ApplyPreferredVersionsAsync(GetAllMovies(), user, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitMoviesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = GetMoviesFromLibrary();
            var current = 0;
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {Name} ({Year})", movie.Name, movie.ProductionYear);
                await _videoVersions.SplitAsync(movie.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)movies.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Scanning for repeated episodes");

            var episodes = GetEpisodesFromLibrary();
            var duplicateEpisodes = episodes
                .GroupBy(GetEpisodeMergeKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1 &&
                                group.Any(episode => episode.PrimaryVersionId == null &&
                                                     !episode.LinkedAlternateVersions.Any()))
                .ToList();

            _logger.LogInformation(
                "Found {EpisodeCount} episodes and {DuplicateGroupCount} duplicate episode groups",
                episodes.Count,
                duplicateEpisodes.Count);

            var current = 0;
            foreach (var episodeGroup in duplicateEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = episodeGroup.First();
                _logger.LogInformation("Merging {Name} ({Year})", first.Name, first.ProductionYear);
                await _videoVersions.MergeAsync(episodeGroup.Select(e => e.Id).ToArray(), user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)duplicateEpisodes.Count * 100);
            }

            await ApplyPreferredVersionsAsync(episodes, user, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Splitting {EpisodeNumber} ({SeriesName})", episode.IndexNumber, episode.SeriesName);
                await _videoVersions.SplitAsync(episode.Id, user, cancellationToken).ConfigureAwait(false);
                progress?.Report(++current / (double)episodes.Count * 100);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        /// <summary>
        /// Makes a version from <see cref="Configuration.PluginConfiguration.PreferredLocations"/>
        /// the default of every merged movie and episode, and clears links to default versions
        /// that no longer exist.
        /// </summary>
        public async Task ApplyPreferredVersionsAsync(IProgress<double> progress, ClaimsPrincipal user = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var videos = GetAllMovies().Cast<Video>().Concat(GetEpisodesFromLibrary()).ToList();
            await ApplyPreferredVersionsAsync(videos, user, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
        }

        private async Task ApplyPreferredVersionsAsync(IEnumerable<Video> items, ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            var videos = items.ToList();

            // A default version deleted from disk leaves its alternates pointing at nothing. They
            // still list normally, but the merge never picks them up again because they look merged.
            var orphans = videos
                .Where(v => v.PrimaryVersionId.HasValue && _libraryManager.GetItemById(v.PrimaryVersionId.Value) is null)
                .ToList();
            foreach (var orphan in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Clearing the missing default version of {Path}", orphan.Path);
                await _videoVersions.ClearMissingPrimaryAsync(orphan.Id, cancellationToken).ConfigureAwait(false);
            }

            var preferred = GetPreferredLocations();
            if (preferred.Length == 0)
            {
                return;
            }

            var defaults = videos
                .Where(v => v.PrimaryVersionId.HasValue && !orphans.Contains(v))
                .Select(v => v.PrimaryVersionId.Value)
                .Distinct()
                .ToList();
            foreach (var defaultId in defaults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_libraryManager.GetItemById(defaultId) is not Video current)
                {
                    continue;
                }

                // Every version of the group, as Jellyfin links them: regrouping a partial list
                // would drop the versions left out of it. Leave groups with an excluded version alone.
                var members = new List<Video> { current };
                members.AddRange(_libraryManager.GetLinkedAlternateVersions(current).Where(v => v.Id != current.Id));
                if (members.Count < 2 || !members.All(IsEligible))
                {
                    continue;
                }
                var chosen = ChoosePreferred(members, current, preferred, _fileSystem.ContainsSubPath);
                if (chosen is null)
                {
                    continue;
                }

                var alternates = members.Where(m => m.Id != chosen.Id).Select(m => m.Id).ToArray();
                _logger.LogInformation("Making {Path} the default version of {Name}", chosen.Path, chosen.Name);
                if (!await _videoVersions.MakePrimaryAsync(chosen.Id, current.Id, alternates, user, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("Jellyfin kept another default version for {Name} ({Path})", chosen.Name, chosen.Path);
                }
            }
        }

        /// <summary>
        /// The version that should be the default of a merged group, or null to keep the current one:
        /// the version in the highest-priority preferred folder, the widest of those on a tie. The
        /// current default stays when it is already in the best folder any version is in.
        /// </summary>
        internal static Video ChoosePreferred(
            IReadOnlyList<Video> members,
            Video current,
            IReadOnlyList<string> preferredLocations,
            Func<string, string, bool> containsSubPath)
        {
            int Rank(Video video)
            {
                if (!string.IsNullOrEmpty(video.Path))
                {
                    for (var i = 0; i < preferredLocations.Count; i++)
                    {
                        if (containsSubPath(preferredLocations[i], video.Path))
                        {
                            return i;
                        }
                    }
                }

                return int.MaxValue;
            }

            var best = members.Min(Rank);
            if (best == int.MaxValue || Rank(current) == best)
            {
                return null;
            }

            return members
                .Where(m => Rank(m) == best)
                .OrderByDescending(m => m.Width)
                .ThenBy(m => m.Id)
                .First();
        }

        private static string[] GetPreferredLocations()
            => (Plugin.Instance.PluginConfiguration.PreferredLocations ?? Array.Empty<string>())
                .Select(l => l?.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToArray();

        private List<Movie> GetAllMovies()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Movie],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .OfType<Movie>()
                .Where(IsEligible)
                .ToList();
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .Select(m => m as Movie)
                .Where(m => m.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsEligible)
                .ToList();
        }

        private static string GetEpisodeMergeKey(Episode episode)
        {
            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                return $"number:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd}";
            }

            foreach (var provider in new[] { "Tvdb", "Tmdb", "Imdb" })
            {
                if (episode.ProviderIds.TryGetValue(provider, out var providerId)
                    && !string.IsNullOrWhiteSpace(providerId))
                {
                    return $"provider:{episode.SeriesName}:{provider}:{providerId}";
                }
            }

            return $"title:{episode.SeriesName}:{episode.SeasonName}:{episode.Name}:{episode.ProductionYear}";
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
            }
        }
    }
}
