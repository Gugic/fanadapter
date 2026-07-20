using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// One slot of the adapter's device pool, rendered live. This is the only
    /// view a user gets of hardware plugged into the adapter rather than the PC
    /// — Windows can't see those devices at all, so if a binding isn't firing,
    /// this panel is where you find out whether the adapter sees the input.
    /// </summary>
    public class DeviceSlotViewModel : INotifyPropertyChanged
    {
        public DeviceSlotViewModel(DeviceSlot device)
        {
            Slot = device.Slot;
            Apply(device);
        }

        public int Slot { get; }

        public string Name { get; private set; }
        public string Identity { get; private set; }
        public string Summary { get; private set; }
        public bool HasHat { get; private set; }
        public bool HasKeyboard { get; private set; }

        public ObservableCollection<ButtonViewModel> Buttons { get; } = new ObservableCollection<ButtonViewModel>();
        public ObservableCollection<AxisViewModel> Axes { get; } = new ObservableCollection<AxisViewModel>();

        private string _hatText = "—";
        public string HatText
        {
            get => _hatText;
            private set { _hatText = value; OnPropertyChanged(); }
        }

        private string _keysText = "—";
        public string KeysText
        {
            get => _keysText;
            private set { _keysText = value; OnPropertyChanged(); }
        }

        public void Apply(DeviceSlot device)
        {
            Name = device.DisplayName;
            Identity = string.Format("slot {0} · {1:X4}:{2:X4}", device.Slot, device.Vid, device.Pid);

            var parts = new System.Collections.Generic.List<string>();
            if (device.ButtonCount > 0) parts.Add(device.ButtonCount + " buttons");
            if (device.AxisCount > 0) parts.Add(device.AxisCount + " axes");
            if (device.HasHat) parts.Add("hat");
            if (device.HasKeyboard) parts.Add("keyboard");

            // The firmware discovers counts lazily from observed reports, so a
            // device that hasn't been touched yet legitimately reports nothing.
            Summary = parts.Count > 0
                ? string.Join(" · ", parts)
                : "no inputs seen yet — move a control";

            HasHat = device.HasHat;
            HasKeyboard = device.HasKeyboard;

            Resize(Buttons, device.ButtonCount, i => new ButtonViewModel(i));
            Resize(Axes, device.AxisCount, i => new AxisViewModel(i));

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Identity));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasHat));
            OnPropertyChanged(nameof(HasKeyboard));
        }

        public void Apply(LiveSlot live)
        {
            // Counts grow as the firmware observes reports, so a live frame can
            // legitimately carry more axes than list_devices announced.
            if (live.Axes != null && live.Axes.Length > Axes.Count)
            {
                Resize(Axes, live.Axes.Length, i => new AxisViewModel(i));
            }

            for (int i = 0; i < Buttons.Count; i++)
            {
                Buttons[i].IsPressed = (live.Buttons & (1u << i)) != 0;
            }

            if (live.Axes != null)
            {
                for (int i = 0; i < live.Axes.Length && i < Axes.Count; i++)
                {
                    Axes[i].Update(live.Axes[i]);
                }
            }

            if (live.HasHat)
            {
                HasHat = true;
                HatText = live.Hat.HasValue ? HidNames.Hat(live.Hat.Value) : "centred";
            }

            if (live.Keys != null)
            {
                HasKeyboard = true;
                // The firmware zero-pads to six slots; zero means "nothing here".
                var pressed = live.Keys.Where(k => k != 0).Select(HidNames.Key).ToArray();
                KeysText = pressed.Length > 0 ? string.Join("  ", pressed) : "—";
            }
        }

        private static void Resize<T>(ObservableCollection<T> collection, int count, Func<int, T> factory)
        {
            while (collection.Count > count) collection.RemoveAt(collection.Count - 1);
            while (collection.Count < count) collection.Add(factory(collection.Count));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ButtonViewModel : INotifyPropertyChanged
    {
        public ButtonViewModel(int index)
        {
            Index = index;
            Label = (index + 1).ToString();
        }

        public int Index { get; }
        public string Label { get; }

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set
            {
                if (_isPressed == value) return;
                _isPressed = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AxisViewModel : INotifyPropertyChanged
    {
        public AxisViewModel(int index)
        {
            Index = index;
            Label = "Axis " + index;
        }

        public int Index { get; }
        public string Label { get; }

        private int _raw;
        public int Raw
        {
            get => _raw;
            private set { _raw = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Observed maximum, used to scale the bar. The firmware reports raw
        /// device counts and a 10-bit pot never exceeds 1023 — scaling against
        /// 65535 would leave every bar looking dead, so the range is learned.
        /// </summary>
        private int _observedMax = 1;
        public int ObservedMax
        {
            get => _observedMax;
            private set { _observedMax = value; OnPropertyChanged(); }
        }

        private double _percent;
        public double Percent
        {
            get => _percent;
            private set { _percent = value; OnPropertyChanged(); }
        }

        public void Update(int value)
        {
            Raw = value;
            if (value > ObservedMax) ObservedMax = value;
            Percent = ObservedMax > 0 ? Math.Round(value * 100.0 / ObservedMax, 1) : 0;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
