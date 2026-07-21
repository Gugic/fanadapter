using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// One of a channel's four binding slots. Edits are pushed to the adapter
    /// immediately (they live in its RAM until saved), which is what makes the
    /// live preview meaningful — you're watching what the firmware will do, not
    /// a local guess.
    /// </summary>
    public class BindingSlotViewModel : INotifyPropertyChanged
    {
        private readonly Action<BindingSlotViewModel, string, object> _push;
        private InputBinding _binding;

        public BindingSlotViewModel(ChannelViewModel channel, int slot, InputBinding binding,
            Action<BindingSlotViewModel, string, object> push)
        {
            Channel = channel;
            Slot = slot;
            _binding = binding;
            _push = push;
        }

        public ChannelViewModel Channel { get; }
        public int Slot { get; }
        public InputBinding Binding => _binding;

        public void Replace(InputBinding binding)
        {
            _binding = binding;
            RaiseAll();
        }

        public bool IsBound => _binding.Type != InputType.None;

        /// <summary>Calibration only applies to an axis; hide it otherwise.</summary>
        public bool IsAxis => _binding.Type == InputType.Axis;

        /// <summary>
        /// Threshold decides pressed/released when an axis drives a gear or
        /// shift channel, so it's only meaningful on a button-shaped channel.
        /// </summary>
        public bool ShowThreshold => IsAxis && Channel.PrefersButton;

        public string Description
        {
            get
            {
                switch (_binding.Type)
                {
                    case InputType.Button: return "Button " + (_binding.Index + 1);
                    case InputType.Axis: return "Axis " + _binding.Index;
                    case InputType.Hat: return "Hat " + HidNames.Hat(_binding.Index);
                    case InputType.Key: return "Key " + HidNames.Key(_binding.Index);
                    default: return "Empty";
                }
            }
        }

        public string DeviceText =>
            IsBound ? string.Format("{0:X4}:{1:X4}", _binding.Vid, _binding.Pid) : string.Empty;

        // ---------- Calibration ----------

        public int RawMin
        {
            get => _binding.RawMin;
            set { _binding.RawMin = value; Push("rawMin", value); }
        }

        public int RawMax
        {
            get => _binding.RawMax;
            set { _binding.RawMax = value; Push("rawMax", value); }
        }

        public int DeadzoneLow
        {
            get => _binding.DeadzoneLow;
            set { _binding.DeadzoneLow = value; Push("deadzoneLow", value); }
        }

        public int DeadzoneHigh
        {
            get => _binding.DeadzoneHigh;
            set { _binding.DeadzoneHigh = value; Push("deadzoneHigh", value); }
        }

        public int Threshold
        {
            get => _binding.Threshold;
            set { _binding.Threshold = value; Push("threshold", value); }
        }

        public bool Invert
        {
            get => _binding.Invert;
            set { _binding.Invert = value; Push("invert", value); }
        }

        private void Push(string field, object value)
        {
            OnPropertyChanged(field == "invert" ? nameof(Invert) : null);
            _push(this, field, value);
        }

        // ---------- Live preview ----------

        private bool _isCapturing;
        public bool IsCapturing
        {
            get => _isCapturing;
            set
            {
                if (_isCapturing == value) return;
                _isCapturing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        private string _captureHint;
        public string CaptureHint
        {
            get => _captureHint;
            set
            {
                // Re-set on every tick while capturing; only the transitions
                // between the three hints are worth telling WPF about.
                if (_captureHint == value) return;
                _captureHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string StatusText => IsCapturing ? (CaptureHint ?? "Listening…") : string.Empty;

        // Guarded like the device panel's: these sit on the 60 Hz path and an
        // unchanged value must not cost a re-render.
        private int _liveRaw;
        public int LiveRaw
        {
            get => _liveRaw;
            private set { if (_liveRaw == value) return; _liveRaw = value; OnPropertyChanged(); }
        }

        private double _liveProcessedPercent;
        public double LiveProcessedPercent
        {
            get => _liveProcessedPercent;
            private set
            {
                if (Math.Abs(_liveProcessedPercent - value) < 0.05) return;
                _liveProcessedPercent = value;
                OnPropertyChanged();
            }
        }

        private bool _liveActive;
        public bool LiveActive
        {
            get => _liveActive;
            private set { if (_liveActive == value) return; _liveActive = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Applies a live frame from the device this slot is bound to. The
        /// processed value runs through the same scaleAxis the firmware uses, so
        /// the bar shows the value the wheelbase will actually receive.
        /// </summary>
        public void ApplyLive(LiveSlot live)
        {
            switch (_binding.Type)
            {
                case InputType.Button:
                    LiveActive = _binding.Index < 32 && (live.Buttons & (1u << _binding.Index)) != 0;
                    LiveProcessedPercent = LiveActive ? 100 : 0;
                    break;

                case InputType.Hat:
                    LiveActive = live.HasHat && live.Hat == _binding.Index;
                    LiveProcessedPercent = LiveActive ? 100 : 0;
                    break;

                case InputType.Key:
                    LiveActive = live.Keys != null && Array.IndexOf(live.Keys, _binding.Index) >= 0;
                    LiveProcessedPercent = LiveActive ? 100 : 0;
                    break;

                case InputType.Axis:
                    if (live.Axes == null || _binding.Index >= live.Axes.Length) return;
                    LiveRaw = live.Axes[_binding.Index];
                    var processed = ScaleAxis.Apply(LiveRaw, _binding);
                    LiveProcessedPercent = Math.Round(processed * 100.0 / 65535.0, 1);
                    LiveActive = Channel.PrefersButton
                        ? processed >= _binding.Threshold
                        : processed > 0;
                    break;
            }
        }

        private void RaiseAll()
        {
            OnPropertyChanged(null);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
