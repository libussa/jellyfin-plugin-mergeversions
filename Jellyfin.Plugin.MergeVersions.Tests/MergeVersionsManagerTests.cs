using Jellyfin.Data.Enums;
using System.Security.Claims;
using Jellyfin.Plugin.MergeVersions.Api;
using Jellyfin.Plugin.MergeVersions.Configuration;
using Jellyfin.Plugin.MergeVersions.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class MergeVersionsManagerTests
{
    [Theory]
    [InlineData("MergeMovies")]
    [InlineData("SplitMovies")]
    [InlineData("MergeEpisodes")]
    [InlineData("SplitEpisodes")]
    public async Task ManualActionsWaitAndForwardUser(string operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), user, cancellation.Token)).Returns(completion.Task);
        versions.Setup(v => v.SplitAsync(It.IsAny<Guid>(), user, cancellation.Token)).Returns(completion.Task);
        using var manager = CreateManager(operation.Contains("Movies"), versions);
        var controller = new MergeVersionsController(manager, NullLogger<MergeVersionsManager>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user, RequestAborted = cancellation.Token }
            }
        };

        var task = operation switch
        {
            "MergeMovies" => controller.MergeMoviesRequestAsync(),
            "SplitMovies" => controller.SplitMoviesRequestAsync(),
            "MergeEpisodes" => controller.MergeEpisodesRequestAsync(),
            _ => controller.SplitEpisodesRequestAsync()
        };

        Assert.False(task.IsCompleted);
        Assert.Single(versions.Invocations);
        completion.SetResult();
        Assert.IsType<NoContentResult>(await task);
        Assert.Equal(operation.StartsWith("Merge") ? 1 : 2, versions.Invocations.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScheduledTasksWaitForNativeMerge(bool movies)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).Returns(completion.Task);
        using var manager = CreateManager(movies, versions);
        var progress = new RecordedProgress();
        var task = movies
            ? new MergeMoviesTask(manager, NullLogger<MergeVersionsManager>.Instance).Execute(default, progress)
            : new MergeEpisodesTask(manager, NullLogger<MergeVersionsManager>.Instance).Execute(default, progress);

        Assert.False(task.IsCompleted);
        Assert.DoesNotContain(100d, progress.Values);
        var ids = Assert.IsType<Guid[]>(Assert.Single(versions.Invocations).Arguments[0]);
        Assert.Equal(2, ids.Length);

        completion.SetResult();
        await task;
        Assert.Equal(100d, progress.Values.Last());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationStopsBeforeNextGroup(bool movies)
    {
        using var cancellation = new CancellationTokenSource();
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, cancellation.Token))
            .Callback(() => cancellation.Cancel()).Returns(Task.CompletedTask);
        using var manager = CreateManager(movies, versions, groups: 2);
        var progress = new RecordedProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => movies
            ? manager.MergeMoviesAsync(progress, cancellationToken: cancellation.Token)
            : manager.MergeEpisodesAsync(progress, cancellationToken: cancellation.Token));

        Assert.Single(versions.Invocations);
        Assert.DoesNotContain(100d, progress.Values);
    }

    [Fact]
    public async Task FailureIsNotReportedAsSuccessfulCompletion()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("native merge failed"));
        using var manager = CreateManager(true, versions);
        var progress = new RecordedProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeMoviesAsync(progress));

        Assert.DoesNotContain(100d, progress.Values);
    }

    [Fact]
    public async Task EpisodesWithDifferentNumbersDoNotMergeEvenWithSameProviderId()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);

        var ep1_hd = new Episode { Id = Guid.NewGuid(), SeriesName = "Chumbak", ParentIndexNumber = 1, IndexNumber = 1 };
        ep1_hd.ProviderIds["Tvdb"] = "473585";
        var ep1_4k = new Episode { Id = Guid.NewGuid(), SeriesName = "Chumbak", ParentIndexNumber = 1, IndexNumber = 1 };
        ep1_4k.ProviderIds["Tvdb"] = "473585";

        var ep2_hd = new Episode { Id = Guid.NewGuid(), SeriesName = "Chumbak", ParentIndexNumber = 1, IndexNumber = 2 };
        ep2_hd.ProviderIds["Tvdb"] = "473585";
        var ep2_4k = new Episode { Id = Guid.NewGuid(), SeriesName = "Chumbak", ParentIndexNumber = 1, IndexNumber = 2 };
        ep2_4k.ProviderIds["Tvdb"] = "473585";

        var items = new List<BaseItem> { ep1_hd, ep1_4k, ep2_hd, ep2_4k };
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);

        var mergedGroups = new List<Guid[]>();
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>()))
            .Callback<Guid[], ClaimsPrincipal, CancellationToken>((ids, u, ct) => mergedGroups.Add(ids))
            .Returns(Task.CompletedTask);

        var manager = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);

        await manager.MergeEpisodesAsync(null);

        Assert.Equal(2, mergedGroups.Count);
        Assert.Contains(mergedGroups, g => g.Contains(ep1_hd.Id) && g.Contains(ep1_4k.Id) && g.Length == 2);
        Assert.Contains(mergedGroups, g => g.Contains(ep2_hd.Id) && g.Contains(ep2_4k.Id) && g.Length == 2);
    }

    [Fact]
    public async Task AlreadyMergedEpisodesAreNotReMerged()
    {
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);

        var primary = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesName = "Chumbak",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            LinkedAlternateVersions = new[] { new LinkedChild { Path = "/path/to/alt" } }
        };
        var secondary = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesName = "Chumbak",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            PrimaryVersionId = primary.Id
        };

        var items = new List<BaseItem> { primary, secondary };
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);
        library.Setup(l => l.GetItemById(primary.Id)).Returns(primary);

        var manager = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);

        await manager.MergeEpisodesAsync(null);

        // MergeAsync should never be called because all episodes in the group are already merged
        versions.Verify(v => v.MergeAsync(It.IsAny<Guid[]>(), It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ManualEntryPointsRequireElevation()
    {
        var authorization = Assert.Single(typeof(MergeVersionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(Policies.RequiresElevation, authorization.Policy);
    }

    private const string Hd = "/media/movies";
    private const string Uhd = "/media/movies4k";

    [Fact]
    public void ChoosePreferredPicksTheVersionInTheFirstPreferredFolder()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920);

        Assert.Same(hd, MergeVersionsManager.ChoosePreferred(new[] { uhd, hd }, uhd, new[] { Hd }, StartsWith));
    }

    [Fact]
    public void ChoosePreferredKeepsADefaultThatIsAlreadyPreferred()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920);

        Assert.Null(MergeVersionsManager.ChoosePreferred(new[] { hd, uhd }, hd, new[] { Hd }, StartsWith));
    }

    [Fact]
    public void ChoosePreferredKeepsJellyfinsChoiceWhenNoVersionIsPreferred()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920);

        Assert.Null(MergeVersionsManager.ChoosePreferred(new[] { uhd, hd }, uhd, new[] { "/media/other" }, StartsWith));
    }

    [Fact]
    public void ChoosePreferredFollowsThePriorityOrder()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920);

        Assert.Same(uhd, MergeVersionsManager.ChoosePreferred(new[] { hd, uhd }, hd, new[] { Uhd, Hd }, StartsWith));
    }

    [Fact]
    public void ChoosePreferredTakesTheWidestOfTheBestFolder()
    {
        var current = Movie(Uhd, 3840);
        var sd = Movie(Hd, 720);
        var hd = Movie(Hd, 1920);

        Assert.Same(hd, MergeVersionsManager.ChoosePreferred(new[] { current, sd, hd }, current, new[] { Hd }, StartsWith));
    }

    [Fact]
    public async Task ApplyMakesThePreferredVersionTheDefault()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920, primary: uhd);
        var (manager, versions, _) = CreatePreferenceManager(new[] { Hd }, new[] { uhd, hd }, (uhd, new[] { hd }));
        versions.Setup(v => v.MakePrimaryAsync(hd.Id, uhd.Id, new[] { uhd.Id }, null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await manager.ApplyPreferredVersionsAsync(null);

        versions.Verify(v => v.MakePrimaryAsync(hd.Id, uhd.Id, new[] { uhd.Id }, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyDoesNothingWithoutPreferredFolders()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920, primary: uhd);
        var (manager, versions, _) = CreatePreferenceManager(Array.Empty<string>(), new[] { uhd, hd }, (uhd, new[] { hd }));

        await manager.ApplyPreferredVersionsAsync(null);

        Assert.Empty(versions.Invocations);
    }

    [Fact]
    public async Task ApplyLeavesGroupsWithAnExcludedVersionAlone()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920, primary: uhd);
        var (manager, versions, _) = CreatePreferenceManager(new[] { Hd }, new[] { uhd, hd }, (uhd, new[] { hd }));
        Plugin.Instance.PluginConfiguration.LocationsExcluded = new[] { Uhd };

        await manager.ApplyPreferredVersionsAsync(null);

        Assert.Empty(versions.Invocations);
    }

    [Fact]
    public async Task ApplyRegroupsEveryLinkedVersionEvenOnesItDidNotList()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920, primary: uhd);
        var other = Movie("/media/extra", 1280, primary: uhd);
        // Only the 1080p copy is listed; the third version is known through Jellyfin's links.
        var (manager, versions, _) = CreatePreferenceManager(new[] { Hd }, new[] { hd }, (uhd, new[] { hd, other }));
        versions.Setup(v => v.MakePrimaryAsync(hd.Id, uhd.Id, It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await manager.ApplyPreferredVersionsAsync(null);

        var alternates = Assert.IsType<Guid[]>(Assert.Single(versions.Invocations).Arguments[2]);
        Assert.Equal(new[] { uhd.Id, other.Id }.OrderBy(i => i), alternates.OrderBy(i => i));
    }

    [Fact]
    public async Task ApplyClearsLinksToMissingDefaults()
    {
        var hd = Movie(Hd, 1920);
        hd.PrimaryVersionId = Guid.NewGuid();
        var (manager, versions, _) = CreatePreferenceManager(new[] { Hd }, new[] { hd });
        versions.Setup(v => v.ClearMissingPrimaryAsync(hd.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await manager.ApplyPreferredVersionsAsync(null);

        versions.Verify(v => v.ClearMissingPrimaryAsync(hd.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(versions.Invocations);
    }

    [Fact]
    public async Task ApplyContinuesWhenJellyfinKeepsAnotherDefault()
    {
        var first = Movie(Uhd, 3840);
        var firstHd = Movie(Hd, 1920, primary: first);
        var second = Movie(Uhd + "/b", 3840);
        var secondHd = Movie(Hd + "/b", 1920, primary: second);
        var (manager, versions, _) = CreatePreferenceManager(
            new[] { Hd }, new[] { first, firstHd, second, secondHd }, (first, new[] { firstHd }), (second, new[] { secondHd }));
        versions.Setup(v => v.MakePrimaryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await manager.ApplyPreferredVersionsAsync(null);

        Assert.Equal(2, versions.Invocations.Count);
    }

    [Fact]
    public async Task MergingMoviesAppliesThePreferenceAfterwards()
    {
        var uhd = Movie(Uhd, 3840);
        var hd = Movie(Hd, 1920);
        uhd.ProviderIds["Tmdb"] = hd.ProviderIds["Tmdb"] = "438631";
        var (manager, versions, _) = CreatePreferenceManager(new[] { Hd }, new[] { uhd, hd }, (uhd, new[] { hd }));
        versions.Setup(v => v.MergeAsync(It.IsAny<Guid[]>(), null, It.IsAny<CancellationToken>()))
            .Callback(() => hd.PrimaryVersionId = uhd.Id) // Jellyfin picks the widest
            .Returns(Task.CompletedTask);
        versions.Setup(v => v.MakePrimaryAsync(hd.Id, uhd.Id, new[] { uhd.Id }, null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await manager.MergeMoviesAsync(null);

        Assert.Equal(new[] { "MergeAsync", "MakePrimaryAsync" }, versions.Invocations.Select(i => i.Method.Name));
    }

    private static bool StartsWith(string parent, string path) => path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static Movie Movie(string folder, int width, Movie primary = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Dune", Path = folder + "/" + Guid.NewGuid().ToString("N") + ".mkv", Width = width };
        if (primary is not null)
        {
            movie.PrimaryVersionId = primary.Id;
        }

        return movie;
    }

    private static (MergeVersionsManager Manager, Mock<IVideoVersions> Versions, Mock<ILibraryManager> Library) CreatePreferenceManager(
        string[] preferred,
        IEnumerable<Movie> listed,
        params (Movie Primary, Movie[] Alternates)[] groups)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);
        Plugin.Instance.PluginConfiguration.PreferredLocations = preferred;

        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var items = listed.ToList();
        library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Movie))))
            .Returns(() => items.Cast<BaseItem>().ToList());
        library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Episode))))
            .Returns(new List<BaseItem>());
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem)null);
        foreach (var (primary, alternates) in groups)
        {
            foreach (var video in alternates.Append(primary))
            {
                library.Setup(l => l.GetItemById(video.Id)).Returns(video);
            }

            library.Setup(l => l.GetLinkedAlternateVersions(primary)).Returns(alternates);
        }

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.ContainsSubPath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>(StartsWith);
        var versions = new Mock<IVideoVersions>(MockBehavior.Strict);
        var manager = new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance, fileSystem.Object, versions.Object);
        return (manager, versions, library);
    }

    private static MergeVersionsManager CreateManager(bool movies, Mock<IVideoVersions> versions, int groups = 1)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(AppContext.BaseDirectory);
        _ = new TestPlugin(paths.Object);

        var items = Enumerable.Range(0, groups * 2).Select(index =>
        {
            Video item = movies ? new Movie() : new Episode();
            item.Id = Guid.NewGuid();
            item.Name = "Test video";
            item.ProviderIds["Tmdb"] = (index / 2).ToString();
            return (BaseItem)item;
        }).ToList();
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);
        return new MergeVersionsManager(library.Object, NullLogger<MergeVersionsManager>.Instance,
            Mock.Of<IFileSystem>(), versions.Object);
    }

    private sealed class RecordedProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    private sealed class TestPlugin : Plugin
    {
        public TestPlugin(IServerApplicationPaths paths) : base(paths, Mock.Of<IXmlSerializer>())
        {
            Configuration = new PluginConfiguration();
        }
    }
}
