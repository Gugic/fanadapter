using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Fanadapter.Core;

namespace Fanadapter.SimHub.UI
{
    /// <summary>
    /// One wheelbase output channel and the up-to-four adapter inputs driving
    /// it. Multiple slots is deliberate: buttons across slots are OR'd and axes
    /// are MAX'd by the firmware, so a gear can be reachable from two different
    /// shifters at once.
    /// </summary>
    public class ChannelViewModel : INotifyPropertyChanged
    {
        public ChannelViewModel(string key, string group, bool prefersButton)
        {
            Key = key;
            Label = HidNames.Channel(key);
            Group = group;
            PrefersButton = prefersButton;
        }

        public string Key { get; }
        public string Label { get; }
        public string Group { get; }

        /// <summary>
        /// Whether this channel is naturally a button (gears, shifts) or an axis
        /// (pedals, handbrake). The firmware accepts either on either — this
        /// only decides which controls the editor puts in front of you.
        /// </summary>
        public bool PrefersButton { get; }

        public ObservableCollection<BindingSlotViewModel> Slots { get; } =
            new ObservableCollection<BindingSlotViewModel>();

        /// <summary>
        /// Slots worth rendering: everything bound, plus one empty slot to bind
        /// into. Showing all four always would quadruple the page for the common
        /// case of one input per channel.
        /// </summary>
        public ObservableCollection<BindingSlotViewModel> VisibleSlots { get; } =
            new ObservableCollection<BindingSlotViewModel>();

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        public void RefreshVisibleSlots()
        {
            var wanted = Slots.Where(s => s.IsBound).ToList();
            var firstEmpty = Slots.FirstOrDefault(s => !s.IsBound);
            if (firstEmpty != null) wanted.Add(firstEmpty);

            VisibleSlots.Clear();
            foreach (var slot in wanted) VisibleSlots.Add(slot);

            OnPropertyChanged(nameof(BoundCount));
            OnPropertyChanged(nameof(SummaryText));
        }

        public int BoundCount => Slots.Count(s => s.IsBound);

        public string SummaryText =>
            BoundCount == 0 ? "not bound" : BoundCount + (BoundCount == 1 ? " input" : " inputs");

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
