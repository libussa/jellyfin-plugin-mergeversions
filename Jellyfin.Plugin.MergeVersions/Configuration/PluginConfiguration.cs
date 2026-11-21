using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public String[] LocationsExcluded { get; set; }

        /// <summary>
        /// Folders whose files should be the default version of a merged item, highest priority
        /// first. A merged item keeps Jellyfin's choice when none of its versions is in one of
        /// these folders. Empty keeps Jellyfin's choice everywhere.
        /// </summary>
        public String[] PreferredLocations { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            PreferredLocations = Array.Empty<String>();
        }
    }
}
