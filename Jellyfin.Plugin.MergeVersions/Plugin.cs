using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.MergeVersions
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages 
    {
        public Plugin(IServerApplicationPaths appPaths, IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Merge Versions (Preferred Default)";

        public static Plugin Instance { get; private set; }

        public override string Description
            => "Merge Versions";

        public PluginConfiguration PluginConfiguration => Configuration;

        private readonly Guid _id = new Guid("d732e032-a5a4-4ec5-bdfc-a973a7c3f795");
        public override Guid Id => _id;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Merge Versions (Preferred Default)",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configurationpage.html"
                }
            };
        }
    }
}
