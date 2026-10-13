using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core.Updates;
using Fanadapter.SimHub.UI;
using Xunit;

namespace Fanadapter.SimHub.Tests
{
    public class PluginUpdateViewModelTests
    {
        [Fact]
        public async Task UpdatesMarshalAndDetachReattachSynchronizesState()
        {
            var queue = new ConcurrentQueue<Action>();
            using (var service = new PluginUpdateService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Fanadapter.SimHub.dll"), "0.1.1", http: new HttpClient(new EmptyHandler())))
            {
                PluginUpdateViewModel pane = null;
                SimHubTestEnvironment.OnSta(() => {
                    pane = new PluginUpdateViewModel(service, new PluginSettings(), () => { }, _ => { }, a => queue.Enqueue(a));
                    pane.Attach(); while (queue.TryDequeue(out var action)) action();
                });
                string original = pane.StatusText;
                await service.CheckAsync();
                Assert.Equal(original, pane.StatusText); // no worker-thread WPF notifications
                SimHubTestEnvironment.OnSta(() => { while (queue.TryDequeue(out var action)) action(); pane.Detach(); });
                Assert.Contains("No stable", pane.StatusText);
                int changes = 0; pane.PropertyChanged += (_, __) => changes++;
                await service.CheckAsync(); Assert.Empty(queue); Assert.Equal(0, changes);
                SimHubTestEnvironment.OnSta(() => { pane.Attach(); while (queue.TryDequeue(out var action)) action(); pane.Detach(); });
                Assert.True(changes > 0); Assert.False(pane.IsBusy); Assert.False(pane.InstallCommand.CanExecute(null));
            }
        }

        [Fact]
        public void UpdatePreferenceStillChangesWhenPersistenceFails()
        {
            SimHubTestEnvironment.OnSta(() => {
                var settings = new PluginSettings(); string log = null;
                var pane = new PluginUpdateViewModel(null, settings, () => throw new IOException("read only"), s => log = s, a => a());
                pane.AutomaticChecks = false;
                Assert.False(settings.CheckPluginUpdatesAutomatically);
                Assert.Contains("could not be saved", log);
            });
        }

        private sealed class EmptyHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
