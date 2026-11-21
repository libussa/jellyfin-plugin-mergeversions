using System.Security.Claims;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MergeVersions.Tests;

public class JellyfinVideoVersionsTests
{
    [Fact]
    public async Task DiscoversNativeActionsAndUsesFreshScopedControllers()
    {
        await using var services = CreateServices();
        var state = services.GetRequiredService<InvocationState>();
        var adapter = services.GetRequiredService<IVideoVersions>();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("user", "admin") }));

        await adapter.MergeAsync(ids, user, default);
        await adapter.SplitAsync(ids[0], null, default);

        Assert.Equal(ids, state.Controllers[0].MergedIds);
        Assert.Equal(ids[0], state.Controllers[1].SplitId);
        Assert.Same(user, state.Controllers[0].User);
        Assert.Empty(state.Controllers[1].User.Claims);
        Assert.NotSame(state.Controllers[0], state.Controllers[1]);
        Assert.NotSame(state.Controllers[0].HttpContext.RequestServices, state.Controllers[1].HttpContext.RequestServices);
        Assert.All(state.Controllers, controller => Assert.True(controller.Disposed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitsForActionBeforeDisposingScopeEvenWhenCancelled(bool merge)
    {
        await using var services = CreateServices();
        var state = services.GetRequiredService<InvocationState>();
        var completion = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Run = () => completion.Task;
        using var cancellation = new CancellationTokenSource();
        var adapter = services.GetRequiredService<IVideoVersions>();

        var operation = merge
            ? adapter.MergeAsync(new[] { Guid.NewGuid(), Guid.NewGuid() }, null, cancellation.Token)
            : adapter.SplitAsync(Guid.NewGuid(), null, cancellation.Token);

        Assert.False(operation.IsCompleted);
        cancellation.Cancel();
        Assert.False(operation.IsCompleted);
        Assert.False(Assert.Single(state.Controllers).Disposed);
        Assert.Equal(cancellation.Token, state.Controllers[0].HttpContext.RequestAborted);

        completion.SetResult(new NoContentResult());
        await operation;
        Assert.True(state.Controllers[0].Disposed);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task NonSuccessIsReportedAsFailure(int status)
    {
        await using var services = CreateServices();
        var state = services.GetRequiredService<InvocationState>();
        state.Run = () => Task.FromResult<ActionResult>(new ObjectResult("native error") { StatusCode = status });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IVideoVersions>().SplitAsync(Guid.NewGuid(), null, default));

        Assert.Contains(status.ToString(), exception.Message);
        Assert.Contains("native error", exception.Message);
        Assert.True(Assert.Single(state.Controllers).Disposed);
    }

    [Fact]
    public async Task NativeExceptionsAreNotWrapped()
    {
        await using var services = CreateServices();
        var expected = new InvalidOperationException("repository failed");
        var state = services.GetRequiredService<InvocationState>();
        state.Run = () => throw expected;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IVideoVersions>().MergeAsync(new[] { Guid.NewGuid(), Guid.NewGuid() }, null, default));

        Assert.Same(expected, actual);
        Assert.True(Assert.Single(state.Controllers).Disposed);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotResolveController()
    {
        await using var services = CreateServices();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            services.GetRequiredService<IVideoVersions>().SplitAsync(Guid.NewGuid(), null, cancellation.Token));

        Assert.Empty(services.GetRequiredService<InvocationState>().Controllers);
    }

    [Fact]
    public async Task MissingServerControllerFailsClearly()
    {
        var descriptors = new Mock<IActionDescriptorCollectionProvider>();
        descriptors.SetupGet(provider => provider.ActionDescriptors)
            .Returns(new ActionDescriptorCollection(Array.Empty<ActionDescriptor>(), 0));
        var adapter = new JellyfinVideoVersions(Mock.Of<IServiceScopeFactory>(), descriptors.Object, Mock.Of<ILibraryManager>());

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => adapter.SplitAsync(Guid.NewGuid(), null, default));

        Assert.Contains("VideosController", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MakePrimarySplitsLinksThePreferredVersionAndMergesWithJellyfin(bool jellyfinKeepsIt)
    {
        var current = new Movie { Id = Guid.NewGuid() };
        var preferred = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = current.Id };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(current.Id)).Returns(current);
        library.Setup(l => l.GetItemById(preferred.Id)).Returns(preferred);
        BaseItem.LibraryManager = library.Object;
        await using var services = CreateServices(library.Object);
        var state = services.GetRequiredService<InvocationState>();
        LinkedChild[] linksAtMerge = null;
        state.Run = () =>
        {
            var controller = state.Controllers[^1];
            if (controller.SplitId == current.Id)
            {
                preferred.PrimaryVersionId = null; // Jellyfin's split
            }
            else if (controller.MergedIds is not null)
            {
                linksAtMerge = preferred.LinkedAlternateVersions;
                if (jellyfinKeepsIt)
                {
                    current.PrimaryVersionId = preferred.Id; // the version with alternates stays the default
                }
                else
                {
                    preferred.PrimaryVersionId = current.Id; // Jellyfin chose the widest anyway
                }
            }

            return Task.FromResult<ActionResult>(new NoContentResult());
        };

        var result = await services.GetRequiredService<IVideoVersions>()
            .MakePrimaryAsync(preferred.Id, current.Id, new[] { current.Id }, null, default);

        Assert.Equal(jellyfinKeepsIt, result);
        Assert.Equal(current.Id, state.Controllers[0].SplitId);
        Assert.Equal(new[] { preferred.Id, current.Id }, state.Controllers[1].MergedIds);
        var link = Assert.Single(linksAtMerge);
        Assert.Equal(current.Id, link.ItemId);
        Assert.Equal(LinkedChildType.LinkedAlternateVersion, link.Type);
        library.Verify(l => l.UpdateItemAsync(preferred, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearMissingPrimaryOnlyClearsLinksToItemsThatAreGone()
    {
        var present = new Movie { Id = Guid.NewGuid() };
        var linkedToPresent = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = present.Id };
        var orphan = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(present.Id)).Returns(present);
        library.Setup(l => l.GetItemById(linkedToPresent.Id)).Returns(linkedToPresent);
        library.Setup(l => l.GetItemById(orphan.Id)).Returns(orphan);
        BaseItem.LibraryManager = library.Object;
        await using var services = CreateServices(library.Object);
        var adapter = services.GetRequiredService<IVideoVersions>();

        await adapter.ClearMissingPrimaryAsync(linkedToPresent.Id, default);
        await adapter.ClearMissingPrimaryAsync(orphan.Id, default);

        Assert.Equal(present.Id, linkedToPresent.PrimaryVersionId);
        Assert.Null(orphan.PrimaryVersionId);
        library.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ServiceProvider CreateServices(ILibraryManager library = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(library ?? Mock.Of<ILibraryManager>());
        services.AddSingleton<InvocationState>();
        services.AddControllers().AddApplicationPart(typeof(VideosController).Assembly).AddControllersAsServices();
        new PluginServiceRegistrator().RegisterServices(services, null);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
