using System.Windows.Controls;

namespace Fanadapter.SimHub.UI
{
    public partial class SettingsControl : UserControl
    {
        public MainViewModel ViewModel { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(FanadapterPlugin plugin) : this()
        {
            ViewModel = new MainViewModel(plugin);
            DataContext = ViewModel;

            // Detaching keeps a closed pane from listening — otherwise every
            // session event fans out to a growing pile of dead view models.
            //
            // But it MUST be paired with a re-attach: WPF raises Unloaded
            // whenever the control leaves the visual tree, navigating to another
            // SimHub page does that, and what comes back is this same instance.
            // Without the Loaded half, returning to the pane gave a UI whose
            // buttons still worked and whose readouts were all frozen.
            Loaded += (s, e) => ViewModel.Attach();
            Unloaded += (s, e) => ViewModel.Detach();
        }
    }
}
