using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core.Updates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core
{
    /// <summary>Published with the firmware release; Pages mirrors the same bytes.</summary>
    public class FirmwareManifest
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("commit")] public string Commit { get; set; }
        [JsonProperty("builtAt")] public string BuiltAt { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("releaseUrl")] public string ReleaseUrl { get; set; }
        [JsonProperty("downloadUrl")] public string DownloadUrl { get; set; }
        internal string ImageUrl { get; set; }
    }

    /// <summary>Finds firmware-v releases. An explicit base URL still supports fork mirrors.</summary>
    public class FirmwareChannel
    {
        public const string DefaultBaseUrl = PluginReleaseInfo.RepositoryUrl + "/";
        internal const int MaximumImageBytes = 1024 * 1024; // application flash bank 1
        private static readonly HttpClient s_http = CreateHttp();
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private FirmwareManifest _selected;

        public FirmwareChannel(string baseUrl = null, HttpClient http = null)
        {
            _http = http ?? s_http;
            if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl.TrimEnd('/') != DefaultBaseUrl.TrimEnd('/'))
                _baseUrl = baseUrl.TrimEnd('/') + "/";
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient(new HttpClientHandler { SslProtocols = SslProtocols.Tls12 })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Fanadapter-Firmware-Updater");
            return http;
        }

        public async Task<FirmwareManifest> TryGetManifestAsync(CancellationToken ct)
        {
            try
            {
                FirmwareManifest manifest;
                if (_baseUrl != null)
                {
                    manifest = JsonConvert.DeserializeObject<FirmwareManifest>(await GetTextAsync(
                        _baseUrl + "firmware/fanadapter-stm32.json", ct).ConfigureAwait(false));
                    if (string.IsNullOrEmpty(manifest?.Version) || manifest.Size <= 0 || manifest.Size > MaximumImageBytes) return null;
                    manifest.ImageUrl = _baseUrl + "firmware/fanadapter-stm32.bin";
                }
                else
                {
                    JObject release = null;
                    for (int page = 1; page <= 10; page++)
                    {
                        string json = await GetTextAsync(PluginReleaseInfo.ReleasesApiUrl + "?per_page=100&page=" + page, ct).ConfigureAwait(false);
                        release = FindRelease(json);
                        if (release != null || JArray.Parse(json).Count < 100) break;
                    }
                    if (release == null) return null;
                    string tag = (string)release["tag_name"];
                    string pageUrl = PluginReleaseInfo.RepositoryUrl + "/releases/tag/" + tag;
                    string downloadBase = PluginReleaseInfo.RepositoryUrl + "/releases/download/" + tag + "/";
                    if (!PluginReleaseInfo.SameUrl((string)release["html_url"], pageUrl)) return null;
                    var metadata = GetAsset(release, "fanadapter-stm32.json", downloadBase, 16384);
                    var binary = GetAsset(release, "fanadapter-stm32.bin", downloadBase, MaximumImageBytes);
                    if (metadata == null || binary == null) return null;
                    manifest = JsonConvert.DeserializeObject<FirmwareManifest>(await GetTextAsync(
                        downloadBase + "fanadapter-stm32.json", ct).ConfigureAwait(false));
                    if (manifest == null || manifest.Version != tag.Substring("firmware-v".Length) ||
                        manifest.Size != (long)binary["size"] || manifest.Sha256 == null ||
                        (string)binary["digest"] != "sha256:" + manifest.Sha256 ||
                        !PluginReleaseInfo.SameUrl(manifest.ReleaseUrl, pageUrl) ||
                        !PluginReleaseInfo.SameUrl(manifest.DownloadUrl, downloadBase + "fanadapter-stm32.bin")) return null;
                    manifest.ImageUrl = manifest.DownloadUrl;
                }
                _selected = manifest;
                return manifest;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        internal static JObject FindRelease(string json) => JArray.Parse(json).OfType<JObject>()
            .Where(r => (bool?)r["draft"] != true && (bool?)r["prerelease"] != true)
            .Where(r => {
                string tag = (string)r["tag_name"];
                return tag != null && tag.StartsWith("firmware-v", StringComparison.Ordinal) &&
                    !tag.Contains("+") && PluginReleaseInfo.TryVersion(tag.Substring("firmware-v".Length), out _, out bool preview) && !preview;
            })
            .OrderByDescending(r => System.Version.Parse(((string)r["tag_name"]).Substring("firmware-v".Length)))
            .FirstOrDefault();

        private static JObject GetAsset(JObject release, string name, string downloadBase, long maximum)
        {
            var matches = (release["assets"] as JArray)?.OfType<JObject>().Where(a => (string)a["name"] == name).ToArray();
            if (matches == null || matches.Length != 1) return null;
            var asset = matches[0];
            return (string)asset["state"] == "uploaded" && (long?)asset["size"] > 0 && (long?)asset["size"] <= maximum &&
                Regex.IsMatch((string)asset["digest"] ?? "", "^sha256:[0-9a-f]{64}$") &&
                PluginReleaseInfo.SameUrl((string)asset["browser_download_url"], downloadBase + name) ? asset : null;
        }

        private async Task<string> GetTextAsync(string url, CancellationToken ct)
        {
            using (var response = await _http.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Verifies the selected release bytes, then strips the DFU suffix.</summary>
        public async Task<byte[]> GetImageAsync(CancellationToken ct, FirmwareManifest manifest = null)
        {
            manifest = manifest ?? _selected;
            if (manifest == null) throw new InvalidOperationException("Check firmware releases before downloading an image.");
            byte[] raw;
            using (var response = await _http.GetAsync(manifest.ImageUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[32768];
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                    {
                        if (output.Length + count > manifest.Size || output.Length + count > MaximumImageBytes)
                            throw new InvalidDataException("Firmware download exceeds the published size.");
                        output.Write(buffer, 0, count);
                    }
                    raw = output.ToArray();
                }
            }
            if (raw.LongLength != manifest.Size) throw new InvalidDataException("Firmware download is incomplete.");
            if (!string.IsNullOrEmpty(manifest.Sha256))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant() != manifest.Sha256)
                        throw new InvalidDataException("Firmware checksum does not match the selected release. Check for updates again.");
            var image = DfuseFlasher.StripDfuSuffix(raw);
            if (!DfuseFlasher.LooksLikeFirmware(image)) throw new InvalidDataException("The firmware has no valid Cortex-M vector table.");
            return image;
        }
    }
}
