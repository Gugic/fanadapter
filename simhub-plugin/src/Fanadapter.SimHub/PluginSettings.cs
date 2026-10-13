using Fanadapter.Core;

namespace Fanadapter.SimHub
{
    /// <summary>
    /// Persisted through SimHub's own settings store (ReadCommonSettings /
    /// SaveCommonSettings), so it must stay a plain serialisable object.
    /// </summary>
    public class PluginSettings
    {
        /// <summary>Last port the user connected to, reused on the next start.</summary>
        public string PortName { get; set; }

        /// <summary>Connect on plugin start without waiting for the user.</summary>
        public bool AutoConnect { get; set; } = true;

        /// <summary>Check stable plugin releases at startup and every six hours; installation is user initiated.</summary>
        public bool CheckPluginUpdatesAutomatically { get; set; } = true;

        /// <summary>
        /// Where the firmware updater looks for the published build. Null/empty = the canonical
        /// GitHub Releases channel (<see cref="FirmwareChannel.DefaultBaseUrl"/>); forks
        /// hosting a manifest/binary mirror can set this to their site base URL.
        /// </summary>
        public string FirmwareUrl { get; set; }

        public DriveSettings Drive { get; set; } = new DriveSettings();
    }
}
