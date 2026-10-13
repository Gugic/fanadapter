using System;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core.Updates
{
    public sealed class PluginUpdateState
    {
        public PluginReleaseInfo Release { get; }
        public string Status { get; }
        public DateTime? LastChecked { get; }
        public bool Busy { get; }
        public bool RestartRequired { get; }

        public PluginUpdateState(PluginReleaseInfo release, string status, DateTime? lastChecked, bool busy, bool restartRequired)
        {
            Release = release; Status = status; LastChecked = lastChecked; Busy = busy; RestartRequired = restartRequired;
        }
    }

    /// <summary>Process-wide background updater; never runs in the serial or pedal timers.</summary>
    public sealed class PluginUpdateService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Timer _timer;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly string _pluginPath;
        private readonly Action<string> _log;
        private readonly object _timerSync = new object();
        private volatile PluginUpdateState _state = new PluginUpdateState(null, "Not checked yet.", null, false, false);
        private volatile bool _disposed;
        private bool _automatic;
        private int _operation;

        public string InstalledVersion { get; }
        public PluginUpdateState State => _state;
        public event Action Changed;

        public PluginUpdateService(string pluginPath, string installedVersion, Action<string> log = null, HttpClient http = null)
        {
            _pluginPath = pluginPath;
            InstalledVersion = installedVersion;
            _log = log ?? (_ => { });
            _http = http ?? new HttpClient(new HttpClientHandler { SslProtocols = SslProtocols.Tls12 });
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _http.MaxResponseContentBufferSize = 1024 * 1024;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fanadapter.SimHub/" + installedVersion.Split('+')[0]);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            try
            {
                if (PluginUpdateInstaller.CleanupAfterRestart(pluginPath))
                    _state = new PluginUpdateState(null, "Recovered an interrupted plugin update. Restart SimHub once more.", null, false, true);
            }
            catch (Exception ex)
            {
                _log("Plugin update recovery: " + ex.Message);
                _state = new PluginUpdateState(null, "Plugin update recovery failed. Restart SimHub or reinstall both release DLLs. " + ex.Message, null, false, true);
            }
            _timer = new Timer(AutomaticCheck, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Configure(bool automatic)
        {
            lock (_timerSync)
            {
                if (_disposed || automatic == _automatic) return;
                _automatic = automatic;
                _timer.Change(automatic ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
                    automatic ? TimeSpan.FromHours(6) : Timeout.InfiniteTimeSpan);
            }
        }

        private void AutomaticCheck(object unused)
        {
            lock (_timerSync) { if (_disposed || !_automatic) return; }
            _ = CheckAsync();
        }

        public Task CheckAsync() => Task.Run(CheckInBackgroundAsync);

        private async Task CheckInBackgroundAsync()
        {
            if (_disposed || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
            var previous = State;
            if (previous.RestartRequired) { Interlocked.Exchange(ref _operation, 0); return; }
            try
            {
                Publish(new PluginUpdateState(previous.Release, "Checking plugin releases…", previous.LastChecked, true, false));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    PluginReleaseInfo release = null;
                    // Firmware and plugin releases share the repository; paginate past firmware
                    // releases so /releases/latest can never select the wrong component.
                    for (int page = 1; page <= 10; page++)
                    {
                        using (var response = await _http.GetAsync(PluginReleaseInfo.ReleasesApiUrl + "?per_page=100&page=" + page, timeout.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            release = PluginReleaseInfo.ParseLatest(json, InstalledVersion);
                            if (release != null || JArray.Parse(json).Count < 100) break;
                            if (page == 10) throw new InvalidOperationException("Too many releases to find the plugin channel. Open GitHub releases manually.");
                        }
                    }
                    string status = release == null ? "No stable plugin release has been published yet." :
                        release.IsNewer ? "Plugin update available: " + InstalledVersion + " → " + release.Version :
                        "Plugin is up to date (" + release.Version + ").";
                    if (release != null && release.IsNewer && !release.CanInstall)
                        status += " This release needs a manual installation; verified DLL assets are missing.";
                    Publish(new PluginUpdateState(release, status, DateTime.Now, false, false));
                    _log(status);
                }
            }
            catch (Exception ex)
            {
                string detail = ex is OperationCanceledException ? "The request timed out." : ex.Message;
                Publish(new PluginUpdateState(previous.Release, "Could not check plugin updates. " + detail, previous.LastChecked, false, false));
                if (!_disposed) _log("Plugin update check: " + detail);
            }
            finally { Interlocked.Exchange(ref _operation, 0); }
        }

        public Task InstallAsync() => Task.Run(InstallInBackgroundAsync);

        private async Task InstallInBackgroundAsync()
        {
            if (_disposed || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
            var previous = State;
            try
            {
                if (previous.RestartRequired || previous.Release == null || !previous.Release.CanInstall) return;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    await PluginUpdateInstaller.InstallAsync(_http, previous.Release, _pluginPath,
                        status => Publish(new PluginUpdateState(previous.Release, status, previous.LastChecked, true, false)),
                        timeout.Token).ConfigureAwait(false);
                }
                Publish(new PluginUpdateState(previous.Release, "Plugin update installed. Restart SimHub to load " + previous.Release.Version + ".",
                    previous.LastChecked, false, true));
                _log("Plugin update installed; restart required.");
            }
            catch (Exception ex)
            {
                string detail = ex is UnauthorizedAccessException ? "The plugin folder is not writable. Install the release manually or run SimHub as administrator." :
                    ex is OperationCanceledException ? "The download timed out." : ex.Message;
                bool pending = PluginUpdateInstaller.HasPendingUpdate(_pluginPath);
                Publish(new PluginUpdateState(previous.Release, "Could not install plugin update. " + detail +
                    (pending ? " Restart SimHub to recover the interrupted update." : ""), previous.LastChecked, false, pending));
                if (!_disposed) _log("Plugin update install: " + detail);
            }
            finally { Interlocked.Exchange(ref _operation, 0); }
        }

        private void Publish(PluginUpdateState state)
        {
            if (_disposed) return;
            _state = state;
            try { Changed?.Invoke(); }
            catch (Exception ex) { _log("Plugin update display: " + ex.Message); }
        }

        public void Dispose()
        {
            lock (_timerSync)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Dispose();
                _shutdown.Cancel();
                _http.Dispose();
            }
        }
    }
}
