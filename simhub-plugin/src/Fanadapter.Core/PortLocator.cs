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

        /// <summary>Adapter product/interface name with its COM number when available.</summary>
        public string Description { get; set; }

        public int? Vid { get; set; }
        public int? Pid { get; set; }

        /// <summary>True for the native Fanadapter USB VID/PID.</summary>
        public bool IsLikelyAdapter { get; set; }

        public override string ToString() =>
            string.IsNullOrEmpty(Description) ? PortName : Description;
    }

    /// <summary>
    /// Lists only native Fanadapter COM ports by exact USB VID/PID. Names are
    /// display text, so older firmware and Windows' cached generic labels still
    /// match. Generic UART bridges cannot identify their downstream firmware.
    /// </summary>
    public static class PortLocator
    {
        // Mirror firmware-stm32/src/firmware_info.h and webconfig's SerialClient filter.
        public const int AdapterVendorId = 0x1209;
        public const int AdapterProductId = 0xFA00;

        private static readonly Regex VidPidPattern =
            new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

        public static List<SerialPortInfo> List()
        {
            var byName = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);

            // GetPortNames supplies the attached ports; WMI supplies USB identities.
            // Without a verified identity a port is never offered as an adapter.
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
                        if (!byName.TryGetValue(portName, out info)) continue;

                        info.Description = name;

                        var match = VidPidPattern.Match(device["PNPDeviceID"] as string ?? string.Empty);
                        if (match.Success)
                        {
                            info.Vid = Convert.ToInt32(match.Groups[1].Value, 16);
                            info.Pid = Convert.ToInt32(match.Groups[2].Value, 16);
                            info.IsLikelyAdapter = IsKnown(info.Vid.Value, info.Pid.Value);
                            if (info.IsLikelyAdapter)
                                info.Description = AdapterDescription(portName, name, BusReportedName(device));
                        }
                    }
                }
            }
            catch (ManagementException)
            {
                // WMI unavailable: unidentified ports stay excluded.
            }
            catch (UnauthorizedAccessException) { /* Unidentified ports stay excluded. */ }

            return FilterAdapters(byName.Values);
        }

        public static bool IsAdapterPort(string portName) =>
            List().Any(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));

        internal static List<SerialPortInfo> FilterAdapters(IEnumerable<SerialPortInfo> ports) =>
            ports.Where(p => p.Vid.HasValue && p.Pid.HasValue && IsKnown(p.Vid.Value, p.Pid.Value))
                .OrderBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string BusReportedName(ManagementObject device)
        {
            // Windows may keep "USB Serial Device" as the driver friendly name.
            // Ask for the bus-reported USB descriptor text without opening the COM port.
            try
            {
                using (var input = device.GetMethodParameters("GetDeviceProperties"))
                {
                    input["devicePropertyKeys"] = new[] { "DEVPKEY_Device_BusReportedDeviceDesc" };
                    using (var output = device.InvokeMethod("GetDeviceProperties", input, null))
                    {
                        var properties = output?["deviceProperties"] as ManagementBaseObject[];
                        return properties?.FirstOrDefault()?["Data"] as string;
                    }
                }
            }
            catch (ManagementException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // USB identity was already verified; a missing display name must not hide the port.
            catch (InvalidOperationException) { return null; }
        }

        internal static string AdapterDescription(string portName, string friendlyName, string busName)
        {
            if (!string.IsNullOrWhiteSpace(busName) &&
                busName.StartsWith("Fanadapter", StringComparison.OrdinalIgnoreCase))
                return busName.Trim() + " (" + portName + ")";
            if (!string.IsNullOrWhiteSpace(friendlyName) &&
                friendlyName.StartsWith("Fanadapter", StringComparison.OrdinalIgnoreCase))
                return friendlyName;
            return "Fanadapter (" + portName + ")";
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
            vid == AdapterVendorId && pid == AdapterProductId;
    }
}
