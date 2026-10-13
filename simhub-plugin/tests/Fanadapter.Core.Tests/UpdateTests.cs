using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Fanadapter.Core.Updates;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Fanadapter.Core.Tests
{
    public class UpdateTests
    {
        private static JObject Release(string tag = "simhub-v0.2.0") => new JObject
        {
            ["tag_name"] = tag, ["html_url"] = PluginReleaseInfo.RepositoryUrl + "/releases/tag/" + tag,
            ["draft"] = false, ["prerelease"] = false, ["body"] = "Changes",
            ["assets"] = new JArray(PluginReleaseInfo.DllNames.Select(n => Asset(n, tag, new byte[2048])))
        };

        private static JObject Asset(string name, string tag, byte[] data) => new JObject
        {
            ["name"] = name, ["state"] = "uploaded", ["size"] = data.Length,
            ["browser_download_url"] = PluginReleaseInfo.RepositoryUrl + "/releases/download/" + tag + "/" + name,
            ["digest"] = "sha256:" + Hash(data)
        };
        private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        private static PluginReleaseInfo Parse(params JObject[] releases) => PluginReleaseInfo.ParseLatest(new JArray(releases).ToString(), "0.1.1");

        [Theory]
        [InlineData("0.2.0", "0.1.1", true)]
        [InlineData("0.1.10", "0.1.9", true)]
        [InlineData("0.1.1", "0.1.1+commit", false)]
        [InlineData("0.1.1", "0.1.1-beta.1", true)]
        [InlineData("0.1.0", "0.1.1", false)]
        [InlineData("0.2.0-beta", "0.1.1", false)]
        [InlineData("00.2.0", "0.1.1", false)]
        [InlineData("garbage", "0.1.1", false)]
        public void OnlyNewerStableVersionsInstall(string next, string current, bool expected) =>
            Assert.Equal(expected, PluginReleaseInfo.IsNewerStable(next, current));

        [Fact]
        public void ComponentChannelsDoNotMixAndIgnoreDraftsPreviews()
        {
            var draft = Release("simhub-v9.0.0"); draft["draft"] = true;
            var preview = Release("simhub-v8.0.0"); preview["prerelease"] = true;
            var releases = new[] { Release("firmware-v20.0.0"), draft, preview, Release("simhub-v0.3.0-beta"), Release(), Release("simhub-v0.1.1") };
            var latest = Parse(releases);
            Assert.Equal("0.2.0", latest.Version); Assert.True(latest.CanInstall);
            Assert.Equal("firmware-v20.0.0", (string)FirmwareChannel.FindRelease(new JArray(releases).ToString())["tag_name"]);
            Assert.Null(Parse(Release("firmware-v20.0.0")));
        }

        [Theory]
        [InlineData("url")]
        [InlineData("digest")]
        [InlineData("size")]
        [InlineData("state")]
        [InlineData("missing")]
        [InlineData("duplicate")]
        public void BothDllsMustHaveUniqueTrustedAssets(string damage)
        {
            var release = Release(); var assets = (JArray)release["assets"]; var asset = (JObject)assets[1];
            if (damage == "url") asset["browser_download_url"] = "https://example.com/Fanadapter.SimHub.dll";
            if (damage == "digest") asset["digest"] = "sha256:123";
            if (damage == "size") asset["size"] = PluginReleaseInfo.MaximumDllBytes + 1;
            if (damage == "state") asset["state"] = "new";
            if (damage == "missing") assets.RemoveAt(1);
            if (damage == "duplicate") assets.Add(asset.DeepClone());
            Assert.True(Parse(release).IsNewer); Assert.False(Parse(release).CanInstall);
        }

        [Fact]
        public void RejectsReleasePageOutsideRepository()
        {
            var release = Release(); release["html_url"] = "https://example.com/release";
            Assert.Throws<InvalidDataException>(() => Parse(release));
        }

        [Fact]
        public void FailedSecondReplacementRestoresFirstAndLeavesUnrelatedFiles()
        {
            using (var files = new Files())
            {
                files.Prepare(); int count = 0;
                Assert.Throws<IOException>(() => PluginUpdateInstaller.ApplyFiles(files.Root, files.Stage, (source, target, backup) =>
                { if (++count == 2) throw new IOException("second file locked"); File.Replace(source, target, backup); }));
                files.AssertOld();
                Assert.False(PluginUpdateInstaller.HasPendingUpdate(files.PluginPath));
                Assert.Equal("keep", File.ReadAllText(Path.Combine(files.Root, "settings.json")));
            }
        }

        [Fact]
        public void CompletedPairKeepsBackupsUntilRestart()
        {
            using (var files = new Files())
            {
                files.Prepare(); PluginUpdateInstaller.ApplyFiles(files.Root, files.Stage);
                foreach (string name in PluginReleaseInfo.DllNames)
                {
                    Assert.Equal("new", File.ReadAllText(Path.Combine(files.Root, name)));
                    Assert.Equal("old", File.ReadAllText(Path.Combine(files.Root, name) + PluginUpdateInstaller.BackupSuffix));
                }
                Assert.True(PluginUpdateInstaller.HasPendingUpdate(files.PluginPath));
                Assert.False(PluginUpdateInstaller.CleanupAfterRestart(files.PluginPath));
                Assert.False(PluginUpdateInstaller.HasPendingUpdate(files.PluginPath));
                Assert.All(PluginReleaseInfo.DllNames, n => Assert.False(File.Exists(Path.Combine(files.Root, n) + PluginUpdateInstaller.BackupSuffix)));
            }
        }

        [Fact]
        public async Task StartupRecoversInterruptedPairAndBlocksFurtherInstallsUntilRestart()
        {
            using (var files = new Files())
            {
                files.Prepare(); string name = PluginReleaseInfo.DllNames[0];
                File.Replace(Path.Combine(files.Stage, name), Path.Combine(files.Root, name), Path.Combine(files.Root, name) + PluginUpdateInstaller.BackupSuffix);
                File.WriteAllBytes(Path.Combine(files.Root, PluginUpdateInstaller.JournalName), new byte[] { 0 });
                using (var service = new PluginUpdateService(files.PluginPath, "0.1.1", http: new HttpClient(new Handler(_ => throw new Exception("must not check")))))
                {
                    files.AssertOld(); Assert.True(service.State.RestartRequired);
                    await service.CheckAsync(); Assert.True(service.State.RestartRequired);
                }
            }
        }

        private static byte[] Dll(Files files, string name)
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(Path.GetFileNameWithoutExtension(name))
                { Version = new Version(0, 2, 0, 0) }, AssemblyBuilderAccess.Save, files.Stage);
            assembly.DefineDynamicModule("Main", name);
            assembly.Save(name);
            byte[] data = File.ReadAllBytes(Path.Combine(files.Stage, name)); File.Delete(Path.Combine(files.Stage, name));
            return data;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InstallerVerifiesBothDownloadsBeforeChangingInstalledFiles(bool corruptSecond)
        {
            using (var files = new Files())
            {
                files.Prepare();
                foreach (string n in PluginReleaseInfo.DllNames) File.Delete(Path.Combine(files.Stage, n));
                var data = PluginReleaseInfo.DllNames.ToDictionary(n => n, n => Dll(files, n));
                var release = Release(); release["assets"] = new JArray(data.Select(d => Asset(d.Key, "simhub-v0.2.0", d.Value)));
                using (var http = new HttpClient(new Handler(request => {
                    string name = Path.GetFileName(request.RequestUri.AbsolutePath);
                    byte[] body = (byte[])data[name].Clone();
                    if (corruptSecond && name == "Fanadapter.SimHub.dll") body[body.Length - 1] ^= 1;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                })))
                {
                    if (corruptSecond)
                    {
                        await Assert.ThrowsAsync<InvalidDataException>(() => PluginUpdateInstaller.InstallAsync(http, Parse(release), files.PluginPath, _ => { }, CancellationToken.None));
                        files.AssertOld(); Assert.False(PluginUpdateInstaller.HasPendingUpdate(files.PluginPath));
                    }
                    else
                    {
                        await PluginUpdateInstaller.InstallAsync(http, Parse(release), files.PluginPath, _ => { }, CancellationToken.None);
                        Assert.All(PluginReleaseInfo.DllNames, n => Assert.Equal(new Version(0, 2, 0, 0), AssemblyName.GetAssemblyName(Path.Combine(files.Root, n)).Version));
                        Assert.True(PluginUpdateInstaller.HasPendingUpdate(files.PluginPath));
                    }
                    Assert.Empty(Directory.GetDirectories(files.Root, ".fanadapter-download-*"));
                }
            }
        }

        [Fact]
        public async Task CheckPaginatesPastFirmwareAndReportsOfflineFailure()
        {
            using (var files = new Files())
            {
                bool offline = false; int requests = 0;
                using (var service = new PluginUpdateService(files.PluginPath, "0.1.1", http: new HttpClient(new Handler(r => {
                    requests++; if (offline) throw new HttpRequestException("offline");
                    var body = r.RequestUri.Query.EndsWith("&page=1", StringComparison.Ordinal)
                        ? new JArray(Enumerable.Range(0, 100).Select(_ => Release("firmware-v0.6.1"))) : new JArray(Release());
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString()) };
                }))))
                {
                    await service.CheckAsync(); Assert.Equal(2, requests); Assert.True(service.State.Release.CanInstall); Assert.False(service.State.Busy);
                    offline = true; await service.CheckAsync(); Assert.Contains("Could not check", service.State.Status); Assert.False(service.State.Busy);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FirmwareUsesOwnReleaseAndRejectsCorruptImage(bool corrupt)
        {
            var image = new byte[4096]; Array.Copy(BitConverter.GetBytes(0x24010000u), 0, image, 0, 4);
            Array.Copy(BitConverter.GetBytes(0x08000101u), 0, image, 4, 4);
            const string tag = "firmware-v0.6.1";
            string page = PluginReleaseInfo.RepositoryUrl + "/releases/tag/" + tag;
            string binary = PluginReleaseInfo.RepositoryUrl + "/releases/download/" + tag + "/fanadapter-stm32.bin";
            var manifest = new JObject { ["version"] = "0.6.1", ["size"] = image.Length, ["sha256"] = Hash(image), ["releaseUrl"] = page, ["downloadUrl"] = binary };
            var release = Release(tag);
            release["assets"] = new JArray(Asset("fanadapter-stm32.bin", tag, image), Asset("fanadapter-stm32.json", tag, System.Text.Encoding.UTF8.GetBytes(manifest.ToString())));
            using (var http = new HttpClient(new Handler(r => {
                if (r.RequestUri.AbsolutePath.EndsWith(".bin"))
                {
                    byte[] body = (byte[])image.Clone(); if (corrupt) body[99] ^= 1;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri.AbsolutePath.EndsWith(".json") ? manifest.ToString() : new JArray(Release("simhub-v99.0.0"), release).ToString()) };
            })))
            {
                var channel = new FirmwareChannel(http: http);
                var selected = await channel.TryGetManifestAsync(CancellationToken.None);
                Assert.Equal("0.6.1", selected.Version);
                if (corrupt) await Assert.ThrowsAsync<InvalidDataException>(() => channel.GetImageAsync(CancellationToken.None, selected));
                else Assert.Equal(image, await channel.GetImageAsync(CancellationToken.None, selected));
            }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => Task.FromResult(_respond(request));
        }
        private sealed class Files : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "fanadapter-update-test-" + Guid.NewGuid().ToString("N"));
            public string Stage => Path.Combine(Root, "stage");
            public string PluginPath => Path.Combine(Root, "Fanadapter.SimHub.dll");
            public Files() { Directory.CreateDirectory(Stage); }
            public void Prepare()
            {
                foreach (string name in PluginReleaseInfo.DllNames) { File.WriteAllText(Path.Combine(Root, name), "old"); File.WriteAllText(Path.Combine(Stage, name), "new"); }
                File.WriteAllText(Path.Combine(Root, "settings.json"), "keep");
            }
            public void AssertOld() => Assert.All(PluginReleaseInfo.DllNames, n => Assert.Equal("old", File.ReadAllText(Path.Combine(Root, n))));
            public void Dispose()
            {
                // Delete only known files and our unique, validated temporary directories.
                foreach (string directory in Directory.GetDirectories(Root).Concat(new[] { Root }))
                {
                    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                    Directory.Delete(directory);
                }
            }
        }
    }
}
