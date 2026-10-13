using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Fanadapter.Core.Updates
{
    public sealed class PluginReleaseAsset
    {
        public string Name { get; internal set; }
        public string DownloadUrl { get; internal set; }
        public long Bytes { get; internal set; }
        public string Sha256 { get; internal set; }
    }

    /// <summary>Pure release decisions; firmware releases never become plugin updates.</summary>
    public sealed class PluginReleaseInfo
    {
        public const string RepositoryUrl = "https://github.com/Gugic/fanadapter";
        public const string ReleasesApiUrl = "https://api.github.com/repos/Gugic/fanadapter/releases";
        public const string TagPrefix = "simhub-v";
        public const int MaximumDllBytes = 16 * 1024 * 1024;
        public static readonly IReadOnlyList<string> DllNames = Array.AsReadOnly(
            new[] { "Fanadapter.Core.dll", "Fanadapter.SimHub.dll" });

        public string Version { get; private set; }
        public string PageUrl { get; private set; }
        public string Notes { get; private set; }
        public IReadOnlyList<PluginReleaseAsset> Assets { get; private set; }
        public bool IsNewer { get; private set; }
        public bool CanInstall => IsNewer && Assets.Count == DllNames.Count;

        public static PluginReleaseInfo ParseLatest(string json, string installedVersion)
        {
            PluginReleaseInfo latest = null;
            foreach (JObject release in JArray.Parse(json).OfType<JObject>())
            {
                string tag = (string)release["tag_name"];
                if (tag == null || !tag.StartsWith(TagPrefix, StringComparison.Ordinal) ||
                    (bool?)release["draft"] == true || (bool?)release["prerelease"] == true) continue;
                if (!TryVersion(tag.Substring(TagPrefix.Length), out var numeric, out var preview) ||
                    preview || tag.Contains("+")) continue;
                var candidate = ParseRelease(release, installedVersion);
                if (latest == null || System.Version.Parse(candidate.Version) > System.Version.Parse(latest.Version))
                    latest = candidate;
            }
            return latest;
        }

        private static PluginReleaseInfo ParseRelease(JObject release, string installedVersion)
        {
            string tag = (string)release["tag_name"];
            string version = tag.Substring(TagPrefix.Length);
            string page = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag);
            if (!SameUrl((string)release["html_url"], page))
                throw new InvalidDataException("The plugin release page is outside this repository.");
            var assets = new List<PluginReleaseAsset>();
            var releaseAssets = release["assets"] as JArray;
            foreach (string name in DllNames)
            {
                var matches = releaseAssets?.OfType<JObject>().Where(a => (string)a["name"] == name).ToArray();
                if (matches == null || matches.Length != 1) continue;
                var asset = matches[0];
                string download = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name;
                string digest = (string)asset["digest"];
                if ((string)asset["state"] != "uploaded" || !SameUrl((string)asset["browser_download_url"], download) ||
                    !long.TryParse((string)asset["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
                    bytes < 1024 || bytes > MaximumDllBytes ||
                    digest == null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) continue;
                assets.Add(new PluginReleaseAsset
                {
                    Name = name, DownloadUrl = download, Bytes = bytes, Sha256 = digest.Substring(7).ToLowerInvariant()
                });
            }
            return new PluginReleaseInfo
            {
                Version = version, PageUrl = page, Notes = (string)release["body"] ?? "No release notes supplied.",
                Assets = assets.AsReadOnly(), IsNewer = IsNewerStable(version, installedVersion)
            };
        }

        public static bool IsNewerStable(string candidate, string installed)
        {
            if (!TryVersion(candidate, out var next, out var nextPreview) || nextPreview ||
                !TryVersion(installed, out var current, out var currentPreview)) return false;
            int comparison = next.CompareTo(current);
            return comparison > 0 || (comparison == 0 && currentPreview);
        }

        internal static bool TryVersion(string text, out System.Version version, out bool preview)
        {
            version = null;
            preview = false;
            var match = Regex.Match(text ?? "", @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$");
            if (!match.Success) return false;
            preview = match.Groups[4].Success;
            return System.Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value, out version);
        }

        internal static bool SameUrl(string text, string expected) =>
            Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            string.Equals(uri.AbsoluteUri, expected, StringComparison.Ordinal);
    }
}
