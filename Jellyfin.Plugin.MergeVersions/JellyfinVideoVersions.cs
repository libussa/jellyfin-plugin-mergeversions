using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MergeVersions
{
    /// <summary>
    /// Calls the installed server's version actions without an HTTP round trip or a bundled Jellyfin.Api assembly.
    /// </summary>
    public sealed class JellyfinVideoVersions : IVideoVersions
    {
        private const string ControllerName = "Jellyfin.Api.Controllers.VideosController";
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly Lazy<(Type Controller, MethodInfo Merge, MethodInfo Split)> _actions;

        public JellyfinVideoVersions(
            IServiceScopeFactory scopeFactory,
            IActionDescriptorCollectionProvider actionDescriptors,
            ILibraryManager libraryManager)
        {
            _scopeFactory = scopeFactory;
            _libraryManager = libraryManager;
            // MVC already knows the controller type from the server's assembly. Resolve it lazily,
            // after both Jellyfin and the plugin have finished registering their controllers.
            _actions = new Lazy<(Type, MethodInfo, MethodInfo)>(() =>
            {
                var type = actionDescriptors.ActionDescriptors.Items
                    .OfType<ControllerActionDescriptor>()
                    .Select(action => action.ControllerTypeInfo.AsType())
                    .Distinct()
                    .SingleOrDefault(candidate => candidate.FullName == ControllerName);

                if (type == null || !typeof(ControllerBase).IsAssignableFrom(type))
                {
                    throw new NotSupportedException("The installed Jellyfin server does not expose VideosController.");
                }

                return (type, GetAction(type, "MergeVersions", typeof(Guid[])),
                    GetAction(type, "DeleteAlternateSources", typeof(Guid)));
            });
        }

        public Task MergeAsync(Guid[] ids, ClaimsPrincipal user, CancellationToken cancellationToken)
            => InvokeAsync(ids, true, user, cancellationToken);

        public Task SplitAsync(Guid itemId, ClaimsPrincipal user, CancellationToken cancellationToken)
            => InvokeAsync(itemId, false, user, cancellationToken);

        public async Task<bool> MakePrimaryAsync(
            Guid primaryId,
            Guid currentPrimaryId,
            Guid[] alternateIds,
            ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alternateIds.Length == 0 || alternateIds.Contains(primaryId))
            {
                throw new ArgumentException("The alternates must be the other versions of the group.", nameof(alternateIds));
            }

            // Jellyfin's merge keeps a version that already has alternates (and is no one's
            // alternate) as the default, and otherwise picks the widest. So split the group, give
            // the preferred version its alternates, and let Jellyfin's own merge do the rest:
            // pointing every alternate at the default and moving playlist and collection entries.
            await SplitAsync(currentPrimaryId, user, cancellationToken).ConfigureAwait(false);

            if (_libraryManager.GetItemById(primaryId) is not Video primary)
            {
                return false;
            }

            primary.LinkedAlternateVersions = alternateIds
                .Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion })
                .ToArray();
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            var ids = new[] { primaryId }.Concat(alternateIds).ToArray();
            await MergeAsync(ids, user, cancellationToken).ConfigureAwait(false);

            return _libraryManager.GetItemById(primaryId) is Video { PrimaryVersionId: null }
                && alternateIds.All(id => _libraryManager.GetItemById(id) is Video alternate
                    && alternate.PrimaryVersionId == primaryId);
        }

        public async Task ClearMissingPrimaryAsync(Guid itemId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_libraryManager.GetItemById(itemId) is not Video video
                || !video.PrimaryVersionId.HasValue
                || _libraryManager.GetItemById(video.PrimaryVersionId.Value) is not null)
            {
                return;
            }

            video.SetPrimaryVersionId(null);
            await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        private async Task InvokeAsync<T>(T argument, bool merge, ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actions = _actions.Value;
            var method = merge ? actions.Merge : actions.Split;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var controller = (ControllerBase)scope.ServiceProvider.GetRequiredService(actions.Controller);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = scope.ServiceProvider,
                    RequestAborted = cancellationToken,
                    // Scheduled jobs intentionally run without a user (Jellyfin's Guid.Empty/system context).
                    // Direct calls do not run MVC authorization filters: manual entry points MUST require elevation.
                    User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
                }
            };

            // A bound delegate preserves the original exception instead of wrapping it in TargetInvocationException.
            var action = method.CreateDelegate<Func<T, Task<ActionResult>>>(controller);
            // Jellyfin's actions currently do not accept cancellation. Finish the current group before cancelling
            // the scan; abandoning the task here could leave writes running after its DI scope is disposed.
            var result = await action(argument).ConfigureAwait(false);
            if (result is not NoContentResult)
            {
                var status = (result as IStatusCodeActionResult)?.StatusCode;
                var detail = (result as ObjectResult)?.Value;
                throw new InvalidOperationException(
                    $"Jellyfin VideosController.{method.Name} failed: {status?.ToString() ?? result?.GetType().Name ?? "null result"}. {detail}");
            }
        }

        private static MethodInfo GetAction(Type controller, string name, Type parameter)
        {
            var method = controller.GetMethod(name, new[] { parameter });
            if (method == null || method.IsStatic || method.ReturnType != typeof(Task<ActionResult>))
            {
                throw new NotSupportedException(
                    $"The installed Jellyfin server has an incompatible VideosController.{name} signature.");
            }

            return method;
        }
    }
}
