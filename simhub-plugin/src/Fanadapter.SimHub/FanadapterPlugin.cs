using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fanadapter.Core;
using GameReaderCommon;
using SimHub.Plugins;

namespace Fanadapter.SimHub
{
    /// <summary>
    /// SimHub-side client for the fanadapter USB HID → Fanatec wheelbase adapter.
    ///
    /// Two halves, split by where the input device is physically plugged in:
    ///   • devices on the PC   — SimHub's own input system maps them, and this
    ///                           plugin translates the result into the firmware's
    ///                           direct-output commands over serial.
    ///   • devices on the adapter's USB host hub — only the firmware can see them,
    ///                           so the plugin exposes the same configuration
    ///                           surface the webconfig UI does.
    /// </summary>
    [PluginDescription("Configure and drive a fanadapter USB HID → Fanatec wheelbase adapter")]
    [PluginAuthor("fanadapter")]
    [PluginName("Fanadapter")]
    public class FanadapterPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsKey = "GeneralSettings";

        public PluginManager PluginManager { get; set; }

        public AdapterSession Session { get; } = new AdapterSession();

        /// <summary>Null until Init has run.</summary>
        public DriveController Drive { get; private set; }

        /// <summary>
        /// Defaulted rather than left null: the settings pane reads this, and
        /// nothing guarantees Init has run first.
        /// </summary>
        public PluginSettings Settings { get; private set; } = new PluginSettings();

        public string LeftMenuTitle => "Fanadapter";

        public ImageSource PictureIcon =>
            new BitmapImage(new Uri("pack://application:,,,/Fanadapter.SimHub;component/Resources/menuicon.png"));

        /// <summary>Latest output state the adapter reported, for the exposed properties.</summary>
        private OutputsState _outputs = new OutputsState { Gear = "gear_N" };

        public void Init(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[Fanadapter] starting");

            Settings = this.ReadCommonSettings(SettingsKey, () => new PluginSettings());

            Session.Outputs += o => _outputs = o;
            Session.LogLine += line => global::SimHub.Logging.Current.Debug("[Fanadapter] " + line);

            Drive = new DriveController(
                Session,
                () => PluginManager,
                () => Settings.Drive,
                line => global::SimHub.Logging.Current.Info("[Fanadapter] " + line));

            AttachProperties();
            AttachActions();

            if (Settings.AutoConnect && !string.IsNullOrEmpty(Settings.PortName))
            {
                TryAutoConnect();
            }
        }

        /// <summary>
        /// Exposed so dashboards and NCalc expressions can see what the adapter
        /// is actually sending to the wheelbase.
        /// </summary>
        private void AttachProperties()
        {
            this.AttachDelegate("Connected", () => Session.IsConnected);
            this.AttachDelegate("Port", () => Session.PortName ?? string.Empty);
            this.AttachDelegate("Firmware", () => Session.Version?.Firmware ?? string.Empty);
            this.AttachDelegate("FirmwareVersion", () => Session.Version?.Version ?? string.Empty);
            this.AttachDelegate("SupportsDirectOutput", () => Session.SupportsDirectOutput);

            this.AttachDelegate("Gear", () => GearLabel(_outputs.Gear));
            this.AttachDelegate("ShiftUp", () => _outputs.ShiftUp);
            this.AttachDelegate("ShiftDown", () => _outputs.ShiftDown);
            this.AttachDelegate("Throttle", () => Percent(_outputs.Throttle));
            this.AttachDelegate("Brake", () => Percent(_outputs.Brake));
            this.AttachDelegate("Clutch", () => Percent(_outputs.Clutch));
            this.AttachDelegate("Handbrake", () => Percent(_outputs.Handbrake));
        }

        /// <summary>
        /// Registers everything the user can bind a control to in SimHub's
        /// Controls UI. Gears are exposed twice on purpose: as input mappings,
        /// which carry press *and* release and so reproduce a real H-pattern
        /// shifter's hold semantics, and as plain actions, which only fire on
        /// press and suit a sequential-style "select this gear and stay there"
        /// button. Nothing here reads a device — SimHub decides when these run.
        /// </summary>
        private void AttachActions()
        {
            foreach (var gear in Schema.GearKeys)
            {
                var channel = gear;                       // capture per iteration
                var label = GearLabel(channel);

                this.AddInputMapping(
                    inputName: "Hold" + Suffix(channel),
                    inputPressed: (a, b) => Drive.GearPressed(channel),
                    inputReleased: (a, b) => Drive.GearReleased(channel));

                this.AddAction(
                    actionName: "Select" + Suffix(channel),
                    actionStart: (a, b) => Drive.SelectGear(channel));
            }

            this.AddAction("ShiftUp", (a, b) => Drive.Shift(ShiftDirection.Up));
            this.AddAction("ShiftDown", (a, b) => Drive.Shift(ShiftDirection.Down));

            // The escape hatch. Overrides are sticky, so a user who binds
            // something wrong needs a way to hand control back without
            // restarting SimHub.
            this.AddAction("ReleaseOutputs", (a, b) => Drive.ReleaseAll());
            this.AddAction("RearmPedals", (a, b) => Drive.RearmPedals());
        }

        /// <summary>"gear_R" → "GearR", "gear_1" → "Gear1" for action names.</summary>
        private static string Suffix(string gearChannel) =>
            "Gear" + GearLabel(gearChannel);

        /// <summary>"gear_3" → "3", "gear_R" → "R" — the form a dashboard wants.</summary>
        private static string GearLabel(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return "N";
            return channel.StartsWith("gear_", StringComparison.Ordinal) ? channel.Substring(5) : channel;
        }

        private static double Percent(int raw) => Math.Round(raw * 100.0 / 65535.0, 1);

        private void TryAutoConnect()
        {
            var port = Settings.PortName;

            // Don't block SimHub's startup on a 2 s port settle plus a handshake,
            // and don't let a missing adapter turn into a failed plugin load.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await Session.ConnectAsync(port);
                    await Session.Protocol.SetLiveOutputsAsync(true);
                    global::SimHub.Logging.Current.Info("[Fanadapter] connected on " + port);
                }
                catch (Exception ex)
                {
                    global::SimHub.Logging.Current.Info(
                        "[Fanadapter] auto-connect to " + port + " failed: " + ex.Message);
                }
            });
        }

        public void SaveSettings() => this.SaveCommonSettings(SettingsKey, Settings);

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        public void End(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[Fanadapter] stopping");
            SaveSettings();

            // Order matters. Overrides are sticky with no firmware timeout, so
            // the wheelbase has to be handed back to its own mapping *before*
            // the port closes — otherwise it sits holding the last gear and
            // pedal positions with nothing left to tell it otherwise.
            Drive?.Dispose();

            // SimHub tears plugins down and rebuilds them on every game change,
            // so this runs often. Releasing the port here is what stops the next
            // Init from finding it locked by our own dead instance.
            Session.Dispose();
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new UI.SettingsControl(this);
    }
}
