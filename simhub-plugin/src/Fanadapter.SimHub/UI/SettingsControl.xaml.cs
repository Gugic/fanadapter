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

            // SimHub builds a fresh settings control each time the pane is
            // opened, so the old one has to stop listening or every session
            // event fans out to a growing pile of dead view models.
            Unloaded += (s, e) => ViewModel.Detach();
        }
    }
}
