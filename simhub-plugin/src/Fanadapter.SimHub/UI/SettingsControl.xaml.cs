using System.Windows.Controls;

namespace Fanadapter.SimHub.UI
{
    public partial class SettingsControl : UserControl
    {
        public FanadapterPlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(FanadapterPlugin plugin) : this()
        {
            Plugin = plugin;
        }
    }
}
