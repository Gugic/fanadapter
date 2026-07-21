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

        public DriveSettings Drive { get; set; } = new DriveSettings();
    }
}
