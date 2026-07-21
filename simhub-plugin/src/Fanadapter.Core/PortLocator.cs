using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace Fanadapter.Core
{
    public class SerialPortInfo
    {
        public string PortName { get; set; }

        /// <summary>Friendly name as Windows reports it, e.g. "USB-SERIAL CH340K (COM16)".</summary>
        public string Description { get; set; }

        public int? Vid { get; set; }
        public int? Pid { get; set; }

        /// <summary>True when the VID/PID matches something the firmware is known to arrive on.</summary>
        public bool IsLikelyAdapter { get; set; }

        public override string ToString() =>
            string.IsNullOrEmpty(Description) ? PortName : Description;
    }

    /// <summary>
    /// Finds candidate COM ports. The firmware can appear behind three different
    /// USB identities — the STM32's native CDC, a Teensy's CDC, or a USB-UART
    /// bridge carrying the STM32's USART1 console — and bridge chips vary by
    /// brand, so this ranks rather than filters: every port stays selectable and
    /// known IDs just float to the top. Confirming a port means asking it for
    /// its version, which is what <see cref="Protocol.GetVersionAsync"/> is for.
    /// </summary>
    public static class PortLocator
    {
        private sealed class KnownId
        {
            public int Vid;
            public int? Pid;
        }

        private static readonly KnownId[] KnownIds =
        {
            new KnownId { Vid = 0x1209, Pid = 0xFA00 }, // STM32 native CDC (pid.codes)
            new KnownId { Vid = 0x16C0 },               // Teensy / PJRC CDC
            new KnownId { Vid = 0x1A86 },               // WCH CH340 family bridge
        };

        private static readonly Regex VidPidPattern =
            new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

        public static List<SerialPortInfo> List()
        {
            var byName = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);

            // SerialPort.GetPortNames is the reliable list; WMI adds the friendly
            // name and hardware IDs but can miss or lag devices, so it's the
            // enrichment pass rather than the source.
            foreach (var name in SerialPort.GetPortNames())
            {
                byName[name] = new SerialPortInfo { PortName = name, Description = name };
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject device in results)
                    {
                        var name = device["Name"] as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        var portName = ExtractPortName(name);
                        if (portName == null) continue;

                        SerialPortInfo info;
                        if (!byName.TryGetValue(portName, out info))
                        {
                            info = new SerialPortInfo { PortName = portName };
                            byName[portName] = info;
                        }

                        info.Description = name;

                        var match = VidPidPattern.Match(device["PNPDeviceID"] as string ?? string.Empty);
                        if (match.Success)
                        {
                            info.Vid = Convert.ToInt32(match.Groups[1].Value, 16);
                            info.Pid = Convert.ToInt32(match.Groups[2].Value, 16);
                            info.IsLikelyAdapter = IsKnown(info.Vid.Value, info.Pid.Value);
                        }
                    }
                }
            }
            catch (ManagementException)
            {
                // WMI unavailable or query rejected — the port list from
                // GetPortNames is still usable, just without friendly names.
            }

            return byName.Values
                .OrderByDescending(p => p.IsLikelyAdapter)
                .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static string ExtractPortName(string friendlyName)
        {
            int open = friendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (open < 0) return null;
            int close = friendlyName.IndexOf(')', open);
            if (close < 0) return null;
            return friendlyName.Substring(open + 1, close - open - 1);
        }

        internal static bool IsKnown(int vid, int pid) =>
            KnownIds.Any(k => k.Vid == vid && (!k.Pid.HasValue || k.Pid.Value == pid));
    }
}
