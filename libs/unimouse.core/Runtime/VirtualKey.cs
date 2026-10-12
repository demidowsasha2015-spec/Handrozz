using System.Diagnostics;

namespace UniMouse.Core.Runtime;

/// <summary>
/// Friendly names for the virtual-key codes macros may legally use.
/// Pure data — used by the WPF step editor to render VK codes as names and to
/// parse user input back into codes. The map covers the safe subset of
/// WinUser.h; security-sensitive keys (LWin/RWin/CapsLock) are deliberately
/// excluded from the lookup so they can never be *picked* in the UI, and the
/// MacroValidator remains the final gate before playback either way.
/// </summary>
public static class VirtualKey
{
    /// <summary>Canonical name for a VK code, or "VK 0xXX" when unmapped.</summary>
    public static string Name(int keyCode) =>
        Names.TryGetValue(keyCode, out var name) ? name : $"VK 0x{keyCode:X2}";

    /// <summary>
    /// Parses a friendly key name (case-insensitive; spaces/underscores/dashes
    /// ignored), single characters ("a", "5"), or hex literals ("0x1A").
    /// Returns false for unknown names. Callers must still run
    /// <see cref="Validation.MacroValidator"/> on the produced macro.
    /// </summary>
    public static bool TryParse(string text, out int keyCode)
    {
        keyCode = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var norm = Normalize(trimmed);

        // Hex literal, e.g. "0x1A".
        if (norm.StartsWith("0X", StringComparison.Ordinal))
        {
            if (int.TryParse(norm.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out var hex)
                && hex is >= 0x01 and <= 0xFE)
            {
                keyCode = hex;
                return true;
            }
            keyCode = 0;
            return false;
        }

        if (Lookup.TryGetValue(norm, out keyCode)) return true;

        // Single ASCII character: 'a' -> 0x41, '5' -> 0x35 (VK == uppercase char).
        if (trimmed.Length == 1)
        {
            var c = char.ToUpperInvariant(trimmed[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                keyCode = c;
                return true;
            }
        }

        keyCode = 0;
        return false;
    }

    private static string Normalize(string s) =>
        s.ToUpperInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

    /// <summary>All named codes sorted ascending — binds directly to combo boxes.</summary>
    public static IReadOnlyList<KeyValuePair<int, string>> AllKnown { get; } =
        Names.OrderBy(kv => kv.Key).ToList();

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x10] = "Shift",
        [0x11] = "Ctrl",
        [0x12] = "Alt",
        [0x13] = "Pause",
        [0x1B] = "Escape",
        [0x20] = "Space",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0x2C] = "PrintScreen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x2F] = "Help",
        [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4",
        [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
        [0x41] = "A", [0x42] = "B", [0x43] = "C", [0x44] = "D", [0x45] = "E",
        [0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I", [0x4A] = "J",
        [0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O",
        [0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T",
        [0x55] = "U", [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y",
        [0x5A] = "Z",
        [0x60] = "NumPad0", [0x61] = "NumPad1", [0x62] = "NumPad2", [0x63] = "NumPad3",
        [0x64] = "NumPad4", [0x65] = "NumPad5", [0x66] = "NumPad6", [0x67] = "NumPad7",
        [0x68] = "NumPad8", [0x69] = "NumPad9",
        [0x6A] = "Multiply", [0x6B] = "Add", [0x6C] = "Separator",
        [0x6D] = "Subtract", [0x6E] = "Decimal", [0x6F] = "Divide",
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4",
        [0x74] = "F5", [0x75] = "F6", [0x76] = "F7", [0x77] = "F8",
        [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11", [0x7B] = "F12",
        [0x7C] = "F13", [0x7D] = "F14", [0x7E] = "F15", [0x7F] = "F16",
        [0x80] = "F17", [0x81] = "F18", [0x82] = "F19", [0x83] = "F20",
        [0x84] = "F21", [0x85] = "F22", [0x86] = "F23", [0x87] = "F24",
        [0xBA] = "Oem1 (;:)",   [0xBB] = "OemPlus (=+)", [0xBC] = "OemComma (<,)",
        [0xBD] = "OemMinus (-_)", [0xBE] = "OemPeriod (>. )", [0xBF] = "OemQuestion (?/)",
        [0xC0] = "OemTilde (`~)", [0xDB] = "OemOpenBrackets ([{)", [0xDC] = "OemPipe (|\\)",
        [0xDD] = "OemCloseBrackets (]})", [0xDE] = "OemQuotes ('\"")",
    };

    static VirtualKey()
    {
        // Defense in depth: the safe map must never offer the keys the validator forbids.
        foreach (var forbidden in Validation.MacroLimits.ForbiddenKeyCodes)
            Debug.Assert(!Names.ContainsKey(forbidden), $"VirtualKey map must not include forbidden VK 0x{forbidden:X2}");
    }

    private static readonly Dictionary<string, int> Lookup = BuildLookup();

    private static Dictionary<string, int> BuildLookup()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, name) in Names)
        {
            // "Oem1 (;:)" style names: register both full and short forms.
            d.TryAdd(Normalize(name), code);
            var space = name.IndexOf(' ');
            if (space > 0) d.TryAdd(Normalize(name[..space]), code);
        }
        return d;
    }
}
