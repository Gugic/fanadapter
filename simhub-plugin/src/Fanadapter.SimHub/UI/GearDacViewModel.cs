using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// The two DAC codes that put the wheelbase in one gear. The H-pattern port
    /// reads a pair of voltages rather than a gear number, so each position is
    /// an (X, Y) point the adapter drives — these are the values to nudge if a
    /// particular gear won't register on a given wheelbase.
    /// </summary>
    public class GearDacViewModel : INotifyPropertyChanged
    {
        private readonly GearDac _model;
        private readonly Action<GearDacViewModel> _push;

        public GearDacViewModel(string gear, GearDac model, Action<GearDacViewModel> push)
        {
            Gear = gear;
            Label = HidNames.Channel(gear);
            _model = model;
            _push = push;
        }

        public string Gear { get; }
        public string Label { get; }

        /// <summary>0..4095 — the STM32 drives a real 12-bit DAC, the Teensy a 12-bit PWM.</summary>
        public int X
        {
            get => _model.X;
            set { _model.X = Clamp(value); OnPropertyChanged(); _push(this); }
        }

        public int Y
        {
            get => _model.Y;
            set { _model.Y = Clamp(value); OnPropertyChanged(); _push(this); }
        }

        private static int Clamp(int value) => value < 0 ? 0 : value > 4095 ? 4095 : value;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
