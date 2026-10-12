using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniMouse.Core.Model;

namespace UniMouse.Wpf.Services;

/// <summary>
/// Persists macros as <c>.unimacro.json</c> files under %LOCALAPPDATA%/UniMouse/macros.
/// The on-disk format is byte-compatible with shared/schemas/macro.schema.json, so the
/// same file can be POSTed to /macro/validate on the Python service unchanged.
/// </summary>
public sealed class MacroStore
{
    public const string FileExtension = ".unimacro.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // names are pinned via JsonPropertyName attributes
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string RootDirectory { get; }

    public MacroStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniMouse", "macros");
        Directory.CreateDirectory(RootDirectory);
    }

    private static readonly JsonSerializerOptions StepJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IReadOnlyList<string> ListIds()
    {
        if (!Directory.Exists(RootDirectory)) return [];
        return Directory
            .EnumerateFiles(RootDirectory, "*" + FileExtension)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MacroDefinition? Load(string fileName)
    {
        var path = ResolveSafe(fileName);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MacroDefinition>(json, StepJsonOpts);
    }

    /// <summary>Persists the macro atomically. Returns the real file name written.</summary>
    public string Save(MacroDefinition macro, string? fileName = null)
    {
        var name = fileName ?? SanitizeFileName(macro.Id);
        var path = ResolveSafe(name);
        var json = JsonSerializer.Serialize(macro, JsonOpts);
        // atomic-ish write: temp + move, so a crash never leaves a half-written macro
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        return Path.GetFileName(path);
    }

    public void Delete(string fileName)
    {
        var path = ResolveSafe(fileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Guards against path traversal via crafted file names.</summary>
    private string ResolveSafe(string fileName)
    {
        var cleaned = Path.GetFileName(fileName); // strips any directory components
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("File name must not be empty.", nameof(fileName));
        return Path.Combine(RootDirectory, cleaned);
    }

    private static string SanitizeFileName(string id)
    {
        var safe = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        if (safe.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)) return safe;
        return safe.TrimEnd('.') + FileExtension;
    }
}
