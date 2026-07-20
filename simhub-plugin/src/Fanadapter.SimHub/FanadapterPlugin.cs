using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle => "Fanadapter";

        public ImageSource PictureIcon =>
            new BitmapImage(new Uri("pack://application:,,,/Fanadapter.SimHub;component/Resources/menuicon.png"));

        public void Init(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[Fanadapter] plugin starting");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
        }

        public void End(PluginManager pluginManager)
        {
            global::SimHub.Logging.Current.Info("[Fanadapter] plugin stopping");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => new UI.SettingsControl(this);
    }
}
