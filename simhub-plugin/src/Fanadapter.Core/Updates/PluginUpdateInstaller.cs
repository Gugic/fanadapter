using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fanadapter.Core.Updates
{
    /// <summary>Validates both DLLs before replacement; records recovery state for the pair.</summary>
    public static class PluginUpdateInstaller
    {
        internal const string BackupSuffix = ".fanadapter-previous";
        internal const string JournalName = ".fanadapter-update.pending";

        public static async Task InstallAsync(HttpClient http, PluginReleaseInfo release, string pluginPath,
            Action<string> status, CancellationToken cancellation)
        {
            if (release == null || !release.CanInstall) throw new InvalidOperationException("No verified plugin update is available.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(pluginPath));
            if (PluginReleaseInfo.DllNames.Any(name => File.Exists(Path.Combine(directory, name) + BackupSuffix)) ||
                File.Exists(Path.Combine(directory, JournalName)))
                throw new IOException("A previous update is pending. Restart SimHub first.");
            string stage = Path.Combine(directory, ".fanadapter-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                foreach (var asset in release.Assets)
                {
                    status("Downloading " + asset.Name + "…");
                    string staged = Path.Combine(stage, asset.Name);
                    using (var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != asset.Bytes)
                            throw new InvalidDataException("The download size does not match the release.");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true))
                        {
                            var buffer = new byte[32768];
                            long total = 0;
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                            {
                                total += count;
                                if (total > asset.Bytes) throw new InvalidDataException("The download exceeds the release size.");
                                await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                            }
                            if (total != asset.Bytes) throw new InvalidDataException("The download is incomplete.");
                        }
                    }
                    cancellation.ThrowIfCancellationRequested();
                    status("Verifying " + asset.Name + "…");
                    using (var sha = SHA256.Create())
                    using (var stream = File.OpenRead(staged))
                    {
                        string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                        if (hash != asset.Sha256) throw new InvalidDataException("The checksum does not match GitHub's release asset.");
                    }
                    // Manifest inspection does not load or execute downloaded assemblies.
                    var assembly = AssemblyName.GetAssemblyName(staged);
                    if (assembly.Name != Path.GetFileNameWithoutExtension(asset.Name) || assembly.Version.ToString(3) != release.Version)
                        throw new InvalidDataException("The downloaded DLL has the wrong name or version.");
                }
                cancellation.ThrowIfCancellationRequested();
                status("Installing plugin update…");
                ApplyFiles(directory, stage);
            }
            finally
            {
                // Only the two fixed DLL paths in our newly-created directory are removed.
                try
                {
                    foreach (string name in PluginReleaseInfo.DllNames)
                    {
                        string file = Path.Combine(stage, name);
                        if (File.Exists(file)) File.Delete(file);
                    }
                    Directory.Delete(stage);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static void ApplyFiles(string directory, string stage, Action<string, string, string> replace = null)
        {
            replace = replace ?? ((source, target, backup) => File.Replace(source, target, backup));
            string journal = Path.Combine(directory, JournalName);
            foreach (string name in PluginReleaseInfo.DllNames)
                if (!File.Exists(Path.Combine(directory, name)) || File.Exists(Path.Combine(directory, name) + BackupSuffix))
                    throw new IOException("Both installed plugin DLLs must exist with no pending backups.");
            // A two-file update is not a single filesystem operation. Keep a durable journal
            // so an interrupted pair can be restored on the next process start.
            using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0); // prepared
                stream.Flush(true);
            }
            try
            {
                foreach (string name in PluginReleaseInfo.DllNames)
                {
                    string destination = Path.Combine(directory, name);
                    replace(Path.Combine(stage, name), destination, destination + BackupSuffix);
                }
                using (var stream = new FileStream(journal, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(1); // both replacements completed
                    stream.Flush(true);
                }
            }
            catch
            {
                RestoreBackups(directory);
                File.Delete(journal);
                throw;
            }
        }

        /// <returns>True when an interrupted update was restored; another restart is required.</returns>
        public static bool CleanupAfterRestart(string pluginPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(pluginPath));
            string journal = Path.Combine(directory, JournalName);
            if (!File.Exists(journal)) return false;
            bool incomplete;
            using (var stream = File.OpenRead(journal)) incomplete = stream.ReadByte() != 1;
            if (incomplete) RestoreBackups(directory);
            else
                foreach (string name in PluginReleaseInfo.DllNames)
                {
                    string backup = Path.Combine(directory, name) + BackupSuffix;
                    if (File.Exists(backup)) File.Delete(backup);
                }
            File.Delete(journal);
            return incomplete;
        }

        internal static bool HasPendingUpdate(string pluginPath) =>
            File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath)), JournalName));

        private static void RestoreBackups(string directory)
        {
            foreach (string name in PluginReleaseInfo.DllNames.Reverse())
            {
                string target = Path.Combine(directory, name), backup = target + BackupSuffix;
                if (File.Exists(backup)) File.Replace(backup, target, null);
            }
        }
    }
}
