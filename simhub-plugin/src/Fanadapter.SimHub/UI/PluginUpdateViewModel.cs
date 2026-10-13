using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fanadapter.Core.Updates;

namespace Fanadapter.SimHub.UI
{
    public sealed class PluginUpdateViewModel : INotifyPropertyChanged
    {
        private readonly PluginUpdateService _service;
        private readonly PluginSettings _settings;
        private readonly Action _save;
        private readonly Action<string> _log;
        private readonly Action<Action> _runOnUi;
        private PluginUpdateState _state;
        private bool _attached;

        public PluginUpdateViewModel(PluginUpdateService service, PluginSettings settings,
            Action save, Action<string> log, Action<Action> runOnUi)
        {
            _service = service; _settings = settings; _save = save; _log = log; _runOnUi = runOnUi;
            _state = service?.State ?? new PluginUpdateState(null, "Plugin updates become available after plugin initialization.", null, false, false);
            CheckCommand = new RelayCommand(() => _ = _service.CheckAsync(), () => _service != null && !_state.Busy && !_state.RestartRequired);
            InstallCommand = new RelayCommand(() => _ = _service.InstallAsync(), () => _service != null && !_state.Busy && !_state.RestartRequired && _state.Release?.CanInstall == true);
            ReleaseNotesCommand = new RelayCommand(OpenReleaseNotes, () => _state.Release != null);
        }

        public string InstalledText => "Installed plugin: " + (_service?.InstalledVersion ?? FanadapterPlugin.PluginVersion);
        public string StatusText => _state.Status;
        public string LastCheckedText => _state.LastChecked.HasValue ? "Last checked: " + _state.LastChecked.Value.ToString("g") : "Not checked yet";
        public string ReleaseNotes => _state.Release?.Notes ?? string.Empty;
        public bool IsBusy => _state.Busy;
        public bool RestartRequired => _state.RestartRequired;
        public RelayCommand CheckCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand ReleaseNotesCommand { get; }

        public bool AutomaticChecks
        {
            get => _settings.CheckPluginUpdatesAutomatically;
            set
            {
                if (value == _settings.CheckPluginUpdatesAutomatically) return;
                _settings.CheckPluginUpdatesAutomatically = value;
                _service?.Configure(value);
                try { _save(); }
                catch (Exception ex) { _log("Plugin update preference could not be saved: " + ex.Message); }
                OnPropertyChanged();
            }
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            if (_service != null) _service.Changed += Refresh;
            Refresh();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            if (_service != null) _service.Changed -= Refresh;
        }

        private void Refresh() => _runOnUi(() =>
        {
            if (!_attached) return;
            if (_service != null) _state = _service.State;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LastCheckedText));
            OnPropertyChanged(nameof(ReleaseNotes));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(RestartRequired));
            CheckCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            ReleaseNotesCommand.RaiseCanExecuteChanged();
        });

        private void OpenReleaseNotes()
        {
            try { Process.Start(new ProcessStartInfo(_state.Release.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { _log("Could not open plugin release notes: " + ex.Message); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
