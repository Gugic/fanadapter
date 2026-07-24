using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// Editor for one pedal channel: which SimHub property feeds it, how that
    /// property's range maps onto the firmware's 0..65535, and what the result
    /// currently is. The live readout is the point — "which property is my brake
    /// on and is it the right way round" is otherwise pure guesswork.
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
        }

        public string Channel { get; }
        public string Label { get; }

        public string PropertyName
        {
            get => _model.PropertyName;
            set
            {
                _model.PropertyName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConfigured));
                _onChanged();
            }
        }

        public double InputMin
        {
            get => _model.InputMin;
            set { _model.InputMin = value; OnPropertyChanged(); _onChanged(); }
        }

        public double InputMax
        {
            get => _model.InputMax;
            set { _model.InputMax = value; OnPropertyChanged(); _onChanged(); }
        }

        public bool Invert
        {
            get => _model.Invert;
            set { _model.Invert = value; OnPropertyChanged(); _onChanged(); }
        }

        public bool IsConfigured => _model.IsConfigured;

        // ---------- Auto-detect ----------

        private bool _isDetecting;
        public bool IsDetecting
        {
            get => _isDetecting;
            set
            {
                _isDetecting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DetectButtonText));
                if (!value) DetectStatus = null;
            }
        }

        public string DetectButtonText => _isDetecting ? "Cancel" : "Detect";

        private string _detectStatus;
        public string DetectStatus
        {
            get => _detectStatus;
            set { _detectStatus = value; OnPropertyChanged(); }
        }

        /// <summary>Fills every field from a completed detection in one go.</summary>
        public void ApplyDetection(AxisDetection d)
        {
            _model.PropertyName = d.PropertyName;
            _model.InputMin = Math.Round(d.InputMin, 3);
            _model.InputMax = Math.Round(d.InputMax, 3);
            _model.Invert = d.Invert;
            OnPropertyChanged(nameof(PropertyName));
            OnPropertyChanged(nameof(InputMin));
            OnPropertyChanged(nameof(InputMax));
            OnPropertyChanged(nameof(Invert));
            OnPropertyChanged(nameof(IsConfigured));
            _onChanged();
        }

        // ---------- Live readout ----------

        private string _rawText = "—";
        public string RawText
        {
            get => _rawText;
            private set { _rawText = value; OnPropertyChanged(); }
        }

        private double _scaledPercent;
        public double ScaledPercent
        {
            get => _scaledPercent;
            private set { _scaledPercent = value; OnPropertyChanged(); }
        }

        private string _scaledText = "—";
        public string ScaledText
        {
            get => _scaledText;
            private set { _scaledText = value; OnPropertyChanged(); }
        }

        /// <summary>Called on a UI timer with the property's current value.</summary>
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
                // Distinguish "no such property" from "property reads zero" —
                // a typo'd name is the most likely setup mistake here.
                RawText = "no such property";
                ScaledText = "—";
                ScaledPercent = 0;
                return;
            }

            RawText = Convert.ToString(rawValue);

            var scaled = _model.Scale(rawValue);
            if (scaled == null)
            {
                RawText = RawText + " (not a number)";
                ScaledText = "—";
                ScaledPercent = 0;
                return;
            }

            ScaledPercent = Math.Round(scaled.Value * 100.0 / 65535.0, 1);
            ScaledText = ScaledPercent.ToString("0.0") + " %";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
