namespace Fanadapter.Core
{
    /// <summary>
    /// Human-readable names for the raw HID values the firmware reports.
    /// Mirrors the same tables in webconfig/src/lib/types.ts so both clients
    /// call the same physical control by the same name.
    /// </summary>
    public static class HidNames
    {
        /// <summary>
        /// Hat directions indexed the way the HID Usage Tables define them:
        /// 0 = North, going clockwise.
        /// </summary>
        public static readonly string[] HatDirections =
        {
            "N", "NE", "E", "SE", "S", "SW", "W", "NW",
        };

        public static string Hat(int direction) =>
            direction >= 0 && direction < HatDirections.Length ? HatDirections[direction] : "?";

        /// <summary>
        /// Pretty-prints a HID Keyboard/Keypad scancode (HID Usage Tables §10).
        /// Falls back to "Key 0xNN" for codes without a friendly name.
        /// </summary>
        public static string Key(int scancode)
        {
            if (scancode >= 0x04 && scancode <= 0x1D) return ((char)('A' + (scancode - 0x04))).ToString();
            if (scancode >= 0x1E && scancode <= 0x26) return ((char)('1' + (scancode - 0x1E))).ToString();
            if (scancode == 0x27) return "0";
            if (scancode >= 0x3A && scancode <= 0x45) return "F" + (scancode - 0x3A + 1);
            if (scancode >= 0x59 && scancode <= 0x61) return "Kp" + (scancode - 0x59 + 1);
            if (scancode == 0x62) return "Kp0";

            switch (scancode)
            {
                case 0x28: return "Enter";
                case 0x29: return "Esc";
                case 0x2A: return "Backspace";
                case 0x2B: return "Tab";
                case 0x2C: return "Space";
                case 0x2D: return "-";
                case 0x2E: return "=";
                case 0x2F: return "[";
                case 0x30: return "]";
                case 0x31: return "\\";
                case 0x33: return ";";
                case 0x34: return "'";
                case 0x35: return "`";
                case 0x36: return ",";
                case 0x37: return ".";
                case 0x38: return "/";
                case 0x39: return "CapsLock";
                case 0x4F: return "Right";
                case 0x50: return "Left";
                case 0x51: return "Down";
                case 0x52: return "Up";
                case 0xE0: return "LCtrl";
                case 0xE1: return "LShift";
                case 0xE2: return "LAlt";
                case 0xE3: return "LGUI";
                case 0xE4: return "RCtrl";
                case 0xE5: return "RShift";
                case 0xE6: return "RAlt";
                case 0xE7: return "RGUI";
            }

            return "Key 0x" + scancode.ToString("X2");
        }

        /// <summary>Label for a channel key, e.g. "gear_R" → "Reverse".</summary>
        public static string Channel(string key)
        {
            switch (key)
            {
                case "gear_R": return "Reverse";
                case "gear_1": return "1st";
                case "gear_2": return "2nd";
                case "gear_3": return "3rd";
                case "gear_4": return "4th";
                case "gear_5": return "5th";
                case "gear_6": return "6th";
                case "gear_7": return "7th";
                case "gear_N": return "Neutral";
                case "shift_up": return "Shift up";
                case "shift_down": return "Shift down";
                case "handbrake": return "Handbrake";
                case "throttle": return "Throttle";
                case "brake": return "Brake";
                case "clutch": return "Clutch";
                default: return key;
            }
        }
    }
}
