using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fanadapter.Core;
using SimHub.Plugins;
using SimHub.Plugins.UI.Axis;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// Native SimHub axis assignment and live preview for one pedal channel.
    /// </summary>
    public class AxisSourceViewModel : INotifyPropertyChanged
    {
        private readonly AxisSource _model;
        private readonly Action _onChanged;

        public AxisSourceViewModel(string channel, string label, AxisSource model, Action onChanged)
        {
            Channel = channel;
            Label = label;
            _model = model;
            _onChanged = onChanged;
            _axis = AxisSourceReader.GetAssignment(model);
            // The stream shares this assignment beyond the pane's lifetime;
            // subscribing weakly keeps it from retaining a discarded pane.
            PropertyChangedEventManager.AddHandler(_axis, OnAxisAssignmentChanged, string.Empty);
        }

        public string Channel { get; }
        public string Label { get; }

        private AxisAssignment _axis;
        public AxisAssignment Axis
        {
            get => _axis;
            set
            {
                if (ReferenceEquals(_axis, value)) return;
                PropertyChangedEventManager.RemoveHandler(_axis, OnAxisAssignmentChanged, string.Empty);
                _axis = value ?? new AxisAssignment();
                AxisSourceReader.SetAssignment(_model, _axis);
                PropertyChangedEventManager.AddHandler(_axis, OnAxisAssignmentChanged, string.Empty);
                OnPropertyChanged();
                SaveAxisAssignment();
            }
        }

        private void OnAxisAssignmentChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AxisAssignment.AxisName) ||
                e.PropertyName == nameof(AxisAssignment.AxisMovement)) SaveAxisAssignment();
        }

        private void SaveAxisAssignment()
        {
            _model.AxisName = _axis.AxisName;
            _model.AxisMovement = _axis.AxisMovement.ToString();
            OnPropertyChanged(nameof(IsConfigured));
            _onChanged();
        }

        public object ReadValue(PluginManager manager) => AxisSourceReader.Read(manager, _model);

        public bool IsConfigured => _model.IsConfigured;

        public double InputMinPercent
        {
            get => _model.InputMinPercent;
            set
            {
                if (_model.InputMinPercent.Equals(value)) return;
                _model.InputMinPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RangeError));
                _onChanged();
            }
        }

        public double InputMaxPercent
        {
            get => _model.InputMaxPercent;
            set
            {
                if (_model.InputMaxPercent.Equals(value)) return;
                _model.InputMaxPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RangeError));
                _onChanged();
            }
        }

        public string RangeError => _model.HasValidRange() ? null : "Use 0–100%, with full travel above released.";

        // ---------- Live readout ----------

        // The readout ticks ~30 times a second, and a pedal at rest reports the
        // same value every time. Suppressing unchanged notifications is what
        // keeps that rate cheaper than the 5 Hz one it replaced.

        private string _rawText = "—";
        public string RawText
        {
            get => _rawText;
            private set
            {
                if (_rawText == value) return;
                _rawText = value;
                OnPropertyChanged();
            }
        }

        private double _scaledPercent;
        public double ScaledPercent
        {
            get => _scaledPercent;
            private set
            {
                // Exact comparison on purpose: this is assigned from a rounded
                // value, so equal readings really are bit-identical.
                if (_scaledPercent.Equals(value)) return;
                _scaledPercent = value;
                OnPropertyChanged();
            }
        }

        private string _scaledText = "—";
        public string ScaledText
        {
            get => _scaledText;
            private set
            {
                if (_scaledText == value) return;
                _scaledText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Called on a UI timer with the native assignment's current value.</summary>
        public void UpdateReadout(object rawValue)
        {
            if (!_model.IsConfigured)
            {
                RawText = "—";
                ScaledText = "—";
                ScaledPercent = 0;
                return;
            }

            if (rawValue == null)
            {
                RawText = "axis unavailable — move the pedal";
                ScaledText = "—";
                ScaledPercent = 0;
                return;
            }

            var scaled = _model.Scale(rawValue);
            if (scaled == null)
            {
                RawText = _model.HasValidRange() ? "invalid axis value" : "invalid input range";
                ScaledText = "—";
                ScaledPercent = 0;
                return;
            }

            RawText = (Convert.ToDouble(rawValue) * 100.0).ToString("0.0") + " %";
            ScaledPercent = Math.Round(scaled.Value * 100.0 / 65535.0, 1);
            ScaledText = ScaledPercent.ToString("0.0") + " %";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
