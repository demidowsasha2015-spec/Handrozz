using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniMouse.Core.Model;

namespace UniMouse.Wpf.Services;

/// <summary>Response envelope mirroring GenerateResponse in the Python service.</summary>
public sealed record GenerateResult(
    bool Rejected,
    string? RejectReason,
    string Explanation,
    IReadOnlyList<string> Warnings,
    MacroDefinition? Macro);

/// <summary>
/// Thin HTTP client for the local UniMouse Qwen service (FastAPI).
/// The service is the AI brain; this app owns execution. All calls are to
/// 127.0.0.1 by default — never expose the service on a public interface.
/// </summary>
public sealed class QwenApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;

    public QwenApiClient(Uri baseUri, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(130); // model inference can be slow
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    public Task<bool> HealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = _http.GetAsync("/health", ct).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>Asks the AI service to turn a natural-language intent into a macro.</summary>
    public async Task<GenerateResult> GenerateAsync(
        string prompt,
        string context = "",
        int screenWidth = 1920,
        int screenHeight = 1080,
        CancellationToken ct = default)
    {
        var payload = new
        {
            prompt,
            context,
            screen_width = screenWidth,
            screen_height = screenHeight,
        };

        using var resp = await _http.PostAsJsonAsync("/macro/generate", payload, JsonOpts, ct)
                                    .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content
            .ReadFromJsonAsync<GenerateResult>(JsonOpts, ct)
            .ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Empty generate response.");
    }

    /// <summary>Server-side cross-check of a locally edited macro (schema + safety rules).</summary>
    public async Task<(bool Valid, IReadOnlyList<string> Errors)> ValidateAsync(
        MacroDefinition macro,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToElement(macro, JsonOpts);
        using var resp = await _http.PostAsJsonAsync(
                "/macro/validate", new { macro = json }, JsonOpts, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var valid = doc.RootElement.GetProperty("valid").GetBoolean();
        var errors = new List<string>();
        foreach (var e in doc.RootElement.GetProperty("errors").EnumerateArray())
        {
            var path = e.TryGetProperty("path", out var p) ? p.GetString() : "?";
            var msg = e.TryGetProperty("message", out var m) ? m.GetString() : "?";
            errors.Add($"{path}: {msg}");
        }
        return (valid, errors);
    }

    public void Dispose() => _http.Dispose();
}
