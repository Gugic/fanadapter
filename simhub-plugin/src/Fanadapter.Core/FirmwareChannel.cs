using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Fanadapter.Core
{
    /// <summary>Written next to fanadapter-stm32.bin by the Pages deploy workflow.</summary>
    public class FirmwareManifest
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("commit")] public string Commit { get; set; }
        [JsonProperty("builtAt")] public string BuiltAt { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
    }

    /// <summary>
    /// The published-firmware source: the GitHub Pages deploy builds the STM32 firmware and
    /// publishes firmware/fanadapter-stm32.{json,bin} beside webconfig, so both clients update
    /// from the same single source of truth. Manifest fetch failing (offline, fork without Pages)
    /// resolves null rather than throwing — the UI shows "unavailable" and the update button
    /// stays disabled.
    /// </summary>
    public class FirmwareChannel
    {
        /// <summary>The canonical deploy; override via the plugin's settings for forks.</summary>
        public const string DefaultBaseUrl = "https://gugic.github.io/fanadapter/";

        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        private readonly string _baseUrl;

        public FirmwareChannel(string baseUrl = null)
        {
            var url = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl;
            _baseUrl = url.EndsWith("/") ? url : url + "/";
        }

        public async Task<FirmwareManifest> TryGetManifestAsync(CancellationToken ct)
        {
            try
            {
                var json = await s_http.GetStringAsync(_baseUrl + "firmware/fanadapter-stm32.json")
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var m = JsonConvert.DeserializeObject<FirmwareManifest>(json);
                return string.IsNullOrEmpty(m?.Version) ? null : m;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Downloads the image with its dfu-suffix already stripped.</summary>
        public async Task<byte[]> GetImageAsync(CancellationToken ct)
        {
            var raw = await s_http.GetByteArrayAsync(_baseUrl + "firmware/fanadapter-stm32.bin")
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var image = DfuseFlasher.StripDfuSuffix(raw);
            if (!DfuseFlasher.LooksLikeFirmware(image))
            {
                throw new InvalidOperationException(
                    "The downloaded firmware image looks corrupt (no Cortex-M vector table).");
            }
            return image;
        }
    }
}
