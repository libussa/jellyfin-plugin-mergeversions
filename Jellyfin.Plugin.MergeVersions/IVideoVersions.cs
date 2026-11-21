using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MergeVersions
{
    public interface IVideoVersions
    {
        Task MergeAsync(Guid[] ids, ClaimsPrincipal user, CancellationToken cancellationToken);

        Task SplitAsync(Guid itemId, ClaimsPrincipal user, CancellationToken cancellationToken);

        /// <summary>
        /// Regroups a merged set so that <paramref name="primaryId"/> becomes its default version.
        /// Returns false when Jellyfin kept another version as the default.
        /// </summary>
        Task<bool> MakePrimaryAsync(Guid primaryId, Guid currentPrimaryId, Guid[] alternateIds, ClaimsPrincipal user, CancellationToken cancellationToken);

        /// <summary>
        /// Clears the link from a version to a default version that no longer exists, so the item
        /// stands alone and can be merged again.
        /// </summary>
        Task ClearMissingPrimaryAsync(Guid itemId, CancellationToken cancellationToken);
    }
}
