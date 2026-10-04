using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Data;
using Fanadapter.Core;
using Fanadapter.SimHub.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.UI.Axis;
using Xunit;
using static Fanadapter.SimHub.Tests.SimHubTestEnvironment;

namespace Fanadapter.SimHub.Tests
{
    public class NativeAxisTests
    {
        [Fact]
        public void StandardPickerReplacementAndClearPersistWithoutChangingPropertySetup()
        {
            OnSta(() =>
            {
                using (var host = new AxisHost())
                {
                    host.Register("Brake", 0.25);
                    var source = new AxisSource { PropertyName = "Old.Brake", InputMax = 65535, Invert = true };
                    var saves = 0;
                    var editor = new AxisSourceViewModel("brake", "Brake", source, () => saves++);
                    var picker = new AxisPickerControl();
                    picker.SetBinding(AxisPickerControl.AxisProperty,
                        new Binding(nameof(AxisSourceViewModel.Axis)) { Source = editor, Mode = BindingMode.TwoWay });
                    editor.UseSimHubAxis = true;
                    picker.Axis = new AxisAssignment
                    {
                        AxisName = "ControlMapperPlugin.Brake", AxisMovement = AxisMovement.MaxToMin,
                    };

                    Assert.Same(picker.Axis, editor.Axis);
                    Assert.Same(picker.Axis, AxisSourceReader.GetAssignment(source));
                    Assert.Equal("ControlMapperPlugin.Brake", source.AxisName);
                    Assert.Equal("MaxToMin", source.AxisMovement);
                    editor.UpdateReadout(editor.ReadValue(host.Manager));
                    Assert.Equal(75, editor.ScaledPercent);
                    Assert.Equal(49151, source.Scale(editor.ReadValue(host.Manager)));

                    // Native Clear mutates the existing assignment rather than
                    // replacing the dependency property.
                    picker.Axis.AxisName = null;
                    Assert.False(editor.IsConfigured);
                    Assert.Null(source.AxisName);
                    Assert.True(saves >= 3);
                    editor.UseSimHubAxis = false;
                    Assert.True(editor.IsConfigured);
                    Assert.Equal("Old.Brake", source.PropertyName);
                    Assert.Equal(65535, source.InputMax);
                    Assert.True(source.Invert);
                }
            });
        }

        [Fact]
        public void SavedAssignmentWorksBeforePaneExistsAndTracksLateRoleRegistration()
        {
            OnSta(() =>
            {
                using (var host = new AxisHost())
                {
                    var source = JsonConvert.DeserializeObject<AxisSource>(
                        "{\"UseSimHubAxis\":true,\"AxisName\":\"ControlMapperPlugin.Brake\",\"AxisMovement\":\"MinToMax\"}");
                    AxisSourceReader.Initialize(new DriveSettings { Brake = source });
                    Assert.Null(AxisSourceReader.Read(host.Manager, source));
                    host.Register("Brake", 0.6);

                    var read = Task.Run(() => source.Scale(AxisSourceReader.Read(host.Manager, source)));
                    PumpUntil(read);
                    Assert.Equal(39321, read.Result);

                    var axis = AxisSourceReader.GetAssignment(source).AxisSource;
                    // UI copies can lag: the stream must use GetCurrentValue's
                    // sampled value, not RegisteredAxis.CurrentValue.
                    Set(axis, "<CurrentValue>k__BackingField", 0.1);
                    Assert.Equal(39321, source.Scale(AxisSourceReader.Read(host.Manager, source)));
                    Set(axis, "<IsConnected>k__BackingField", false);
                    Assert.Null(AxisSourceReader.Read(host.Manager, source));
                }
            });
        }

        [Theory]
        [InlineData(6, "stream_axes")]
        [InlineData(5, "set_outputs")]
        public void PedalCommandsUseIndependentlySelectedNativeSources(int protocolVersion, string commandName)
        {
            OnSta(() =>
            {
                using (var host = new AxisHost())
                {
                    host.Register("Throttle", 0.25);
                    host.Register("Brake", 0.75);
                    var settings = new DriveSettings
                    {
                        Throttle = new AxisSource { UseSimHubAxis = true, AxisName = "ControlMapperPlugin.Throttle" },
                        Brake = new AxisSource { UseSimHubAxis = true, AxisName = "ControlMapperPlugin.Brake" },
                    };
                    AxisSourceReader.Initialize(settings);
                    var pipe = new ReplyTransport();
                    using (var client = new SerialClient(pipe))
                    {
                        var session = new AdapterSession();
                        Set(session, "_protocol", new Protocol(client));
                        Set(session, "<Version>k__BackingField", new VersionInfo { Firmware = "fanadapter-stm32", Protocol = protocolVersion });
                        using (var drive = new DriveController(session, () => host.Manager, () => settings, _ => { }))
                        {
                            var tick = (Task)typeof(DriveController).GetMethod("SendTickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                                .Invoke(drive, null);
                            tick.GetAwaiter().GetResult();
                            Assert.Equal(commandName, (string)pipe.LastCommand["cmd"]);
                            Assert.Equal(16384, (int)pipe.LastCommand["throttle"]);
                            Assert.Equal(49151, (int)pipe.LastCommand["brake"]);
                            Assert.Null(pipe.LastCommand["clutch"]);
                            Assert.Null(pipe.LastCommand["handbrake"]);
                        }
                    }
                }
            });
        }

        private sealed class AxisHost : IDisposable
        {
            private static readonly FieldInfo InstanceField = typeof(PluginManager).GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
            private readonly PluginManager _previous;
            public readonly PluginManager Manager;

            public AxisHost()
            {
                // No SimHub process or controllers are started. Initialize just
                // the registry exercised by SimHub's public assignment API.
                Manager = (PluginManager)FormatterServices.GetUninitializedObject(typeof(PluginManager));
                Set(Manager, "AxisValues", new Dictionary<string, RegisteredAxis>());
                _previous = (PluginManager)InstanceField.GetValue(null);
                InstanceField.SetValue(null, Manager);
            }

            public void Register(string role, double value)
            {
                var mapperType = typeof(PluginManager).Assembly.GetType("SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin");
                Manager.AttachAxisDelegate(role, () => value, () => true, mapperType, true);
            }

            public void Dispose() { InstanceField.SetValue(null, _previous); }
        }

        private sealed class ReplyTransport : ISerialTransport
        {
            public JObject LastCommand;
            public bool IsOpen => true;
            public event Action<string> DataReceived;
            public event Action<Exception> ErrorOccurred { add { } remove { } }
            public void Open() { }
            public void Close() { }
            public void Dispose() { }
            public void Write(string text)
            {
                LastCommand = JObject.Parse(text);
                if ((string)LastCommand["cmd"] != "stream_axes") DataReceived?.Invoke("{\"ok\":true}\n");
            }
        }
    }
}
