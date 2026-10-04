using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Fanadapter.Core;
using Fanadapter.SimHub.UI;
using Newtonsoft.Json.Linq;
using Xunit;
using static Fanadapter.SimHub.Tests.SimHubTestEnvironment;

namespace Fanadapter.SimHub.Tests
{
    public class DriveLifecycleTests
    {
        [Fact]
        public void UserReleaseStopsAndReleasesEvenWhenSettingsCannotBeSaved()
        {
            OnSta(() =>
            {
                using (var rig = new Rig())
                {
                    // There is no running SimHub settings host in these tests.
                    // Confirm persistence fails before exercising the user action.
                    Assert.ThrowsAny<Exception>(() => rig.Plugin.SaveSettings());
                    rig.Plugin.Settings.Drive.AxisStreamingEnabled = true;
                    rig.Drive.StartStreaming();
                    Assert.True(rig.Drive.IsStreaming);

                    rig.Plugin.ReleaseOutputsByUser();

                    Assert.False(rig.Drive.IsStreaming);
                    Assert.False(rig.Plugin.Settings.Drive.AxisStreamingEnabled);
                    Assert.Contains(rig.Pipe.Commands, c => (string)c["cmd"] == "release_outputs");
                }
            });
        }

        [Fact]
        public void ReopeningPreservesCapturedClearedAndPulseWidthEdits()
        {
            OnSta(() =>
            {
                using (var rig = new Rig())
                {
                    var channel = rig.Pane.Channels.Single(c => c.Key == "shift_up");
                    var slot = channel.Slots[0];
                    var t0 = DateTime.UtcNow.AddSeconds(-1);
                    var capture = new CaptureEngine("shift_up", 0, t0,
                        _ => new DeviceSlot { Slot = 0, Connected = true, Vid = 123, Pid = 456 });
                    capture.Observe(new LiveSlot { Slot = 0, Buttons = 0, Axes = new int[0] }, t0);
                    capture.Tick(t0.AddMilliseconds(500));
                    capture.Observe(new LiveSlot { Slot = 0, Buttons = 4, Axes = new int[0] },
                        t0.AddMilliseconds(600));
                    Assert.Equal(CapturePhase.Committed, capture.Phase);
                    Rig.Set(rig.Pane, "_capture", capture);
                    Rig.Set(rig.Pane, "_captureTarget", slot);
                    Rig.Invoke(rig.Pane, "PumpCapture");
                    rig.Pane.PulseMs = 150;

                    rig.Reopen();

                    Assert.Same(slot, channel.Slots[0]);
                    Assert.True(channel.Slots[0].IsBound);
                    Assert.Equal(2, channel.Slots[0].Binding.Index);
                    Assert.Equal(150, rig.Pane.PulseMs);
                    Assert.Contains(rig.Pipe.Commands, c => (string)c["cmd"] == "set_binding");
                    Assert.Contains(rig.Pipe.Commands, c => (string)c["cmd"] == "set_pulse_ms");

                    // Start with a populated get_config snapshot: clearing must
                    // not resurrect that binding when the same pane returns.
                    var config = new Config { PulseMs = 75 };
                    config.ShiftUp.Slots.Add(new InputBinding { Type = InputType.Button, Index = 1 });
                    Rig.Set(rig.Plugin.Session, "<Config>k__BackingField", config);
                    rig.Reopen();
                    Assert.True(channel.Slots[0].IsBound);
                    Rig.Invoke(rig.Pane, "ClearBinding", channel.Slots[0]);
                    rig.Reopen();
                    Assert.False(channel.Slots[0].IsBound);
                }
            });
        }

        [Fact]
        public void ReopeningLoadsANewConnectionOrReloadedConfig()
        {
            OnSta(() =>
            {
                using (var rig = new Rig())
                {
                    rig.Pane.PulseMs = 150;
                    rig.Pane.Detach();
                    var config = new Config { PulseMs = 80 };
                    config.ShiftUp.Slots.Add(new InputBinding { Type = InputType.Button, Index = 7 });
                    Rig.Set(rig.Plugin.Session, "<Config>k__BackingField", config);
                    rig.Pane.Attach();

                    Assert.Equal(80, rig.Pane.PulseMs);
                    Assert.Equal(7, rig.Pane.Channels.Single(c => c.Key == "shift_up").Slots[0].Binding.Index);
                }
            });
        }

        [Fact]
        public void AutomaticResumeUpdatesWpfBindingAndDetachRemovesSubscription()
        {
            OnSta(() =>
            {
                using (var rig = new Rig())
                {
                    var button = new Button();
                    button.SetBinding(ContentControl.ContentProperty,
                        new Binding(nameof(MainViewModel.StreamingButtonText)) { Source = rig.Pane });
                    Assert.Equal("Start driving pedals", button.Content);
                    var notifications = 0;
                    var uiDispatcher = Dispatcher.CurrentDispatcher;
                    rig.Pane.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName != nameof(MainViewModel.IsStreaming)) return;
                        Assert.True(uiDispatcher.CheckAccess());
                        notifications++;
                    };
                    rig.Plugin.Settings.Drive.AxisStreamingEnabled = true;
                    rig.Plugin.Settings.Drive.Throttle.PropertyName = "TestThrottle";
                    rig.Pane.Attach(); // Must not subscribe twice.

                    PumpUntil(Task.Run(() => rig.Drive.ResumeIfEnabled()));
                    Assert.True(rig.Drive.IsStreaming);
                    Assert.Equal("Stop driving pedals", button.Content);
                    Assert.Equal(1, notifications);

                    PumpUntil(Task.Run(() => rig.Drive.StopStreaming()));
                    Assert.Equal("Start driving pedals", button.Content);
                    Assert.Equal(2, notifications);

                    rig.Pane.Detach();
                    PumpUntil(Task.Run(() => rig.Drive.ResumeIfEnabled()));
                    Assert.Equal(2, notifications);
                    rig.Pane.Attach();
                    Assert.Equal("Stop driving pedals", button.Content);
                }
            });
        }

        private sealed class Rig : IDisposable
        {
            private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            public readonly FanadapterPlugin Plugin;
            public readonly DriveController Drive;
            public readonly MainViewModel Pane;
            public readonly ReplyTransport Pipe = new ReplyTransport();
            private readonly SerialClient _client;

            public Rig()
            {
                Plugin = new FanadapterPlugin();
                _client = new SerialClient(Pipe);
                Set(Plugin.Session, "_protocol", new Protocol(_client));
                Set(Plugin.Session, "<State>k__BackingField", ConnectionState.Connected);
                Set(Plugin.Session, "<Version>k__BackingField", new VersionInfo { Firmware = "fanadapter-stm32", Protocol = 6 });
                Set(Plugin.Session, "<Config>k__BackingField", new Config { PulseMs = 50 });
                Drive = new DriveController(Plugin.Session, () => null, () => Plugin.Settings.Drive, _ => { });
                Set(Plugin, "<Drive>k__BackingField", Drive);

                // Exercise the real lifecycle methods without the constructor's
                // firmware download, COM discovery or physical connection.
                Pane = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
                Set(Pane, "_plugin", Plugin);
                Set(Pane, "_session", Plugin.Session);
                Set(Pane, "_dispatcher", Dispatcher.CurrentDispatcher);
                Set(Pane, "_lastOutputs", new OutputsState { Gear = "gear_N" });
                foreach (var field in typeof(MainViewModel).GetFields(PrivateInstance))
                {
                    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(ObservableCollection<>))
                        field.SetValue(Pane, Activator.CreateInstance(field.FieldType));
                    else if (field.FieldType == typeof(RelayCommand))
                        field.SetValue(Pane, new RelayCommand(() => { }));
                    else if (field.FieldType == typeof(ParameterCommand))
                        field.SetValue(Pane, new ParameterCommand(_ => { }));
                }
                Set(Pane, "_liveTimer", new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
                    { Interval = TimeSpan.FromSeconds(1) });
                Set(Pane, "_slowTimer", new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
                    { Interval = TimeSpan.FromSeconds(1) });
                Invoke(Pane, "BuildChannels");
                Pane.Attach();
            }

            public void Reopen() { Pane.Detach(); Pane.Attach(); }
            public static void Set(object target, string field, object value) =>
                target.GetType().GetField(field, PrivateInstance).SetValue(target, value);
            public static void Invoke(object target, string method, params object[] arguments) =>
                target.GetType().GetMethod(method, PrivateInstance).Invoke(target, arguments);
            public void Dispose() { Pane.Detach(); Drive.Dispose(); _client.Dispose(); }
        }

        private sealed class ReplyTransport : ISerialTransport
        {
            public readonly ConcurrentQueue<JObject> Commands = new ConcurrentQueue<JObject>();
            public bool IsOpen => true;
            public event Action<string> DataReceived;
            public event Action<Exception> ErrorOccurred { add { } remove { } }
            public void Open() { }
            public void Close() { }
            public void Dispose() { }
            public void Write(string text)
            {
                var command = JObject.Parse(text);
                Commands.Enqueue(command);
                if ((string)command["cmd"] != "stream_axes") DataReceived?.Invoke("{\"ok\":true}\n");
            }
        }
    }
}
