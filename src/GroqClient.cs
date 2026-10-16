using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dictee;

internal sealed class GroqClient
{
    const string Model = "whisper-large-v3";

    // Whisper invents these on near-silent audio (subtitle credits seen in its training data).
    static readonly string[] Hallucinations =
    [
        "amara.org", "sous-titres réalisés", "sous-titrage", "merci d'avoir regardé", "abonnez-vous", "vocabulaire :",
    ];

    readonly HttpClient http = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
    })
    {
        BaseAddress = new Uri("https://api.groq.com/openai/v1/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    long lastContact = long.MinValue;

    public static bool HasApiKey => ApiKey != null;

    static string? ApiKey =>
        Environment.GetEnvironmentVariable("GROQ_API_KEY")
        ?? Environment.GetEnvironmentVariable("GROQ_API_KEY", EnvironmentVariableTarget.User);

    /// <summary>Opens the TLS connection while the user is still speaking, so the upload starts immediately.</summary>
    public void WarmUp()
    {
        if (Environment.TickCount64 - lastContact < 20_000) return;
        lastContact = Environment.TickCount64;
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "models");
                using var response = await http.SendAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        });
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, string prompt)
    {
        var apiKey = ApiKey ?? throw new DictationException("Clé GROQ_API_KEY manquante");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        if (language.Length > 0) form.Add(new StringContent(language), "language");
        if (prompt.Length > 0) form.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        string body;
        HttpStatusCode status;
        try
        {
            using var response = await http.SendAsync(request);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
            throw new DictationException("Pas de connexion");
        }
        catch (TaskCanceledException)
        {
            throw new DictationException("Groq ne répond pas");
        }
        lastContact = Environment.TickCount64;

        if (status != HttpStatusCode.OK)
        {
            Log.Write($"Groq {(int)status}: {body}");
            throw new DictationException(status switch
            {
                HttpStatusCode.Unauthorized => "Clé API Groq invalide",
                HttpStatusCode.TooManyRequests => "Limite Groq atteinte",
                HttpStatusCode.RequestEntityTooLarge => "Enregistrement trop long",
                _ => $"Erreur Groq {(int)status}",
            });
        }

        return Clean(ExtractText(body));
    }

    static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return root.GetProperty("text").GetString() ?? "";

        var text = new StringBuilder();
        foreach (var segment in segments.EnumerateArray())
        {
            double noSpeech = segment.TryGetProperty("no_speech_prob", out var ns) ? ns.GetDouble() : 0;
            double logProb = segment.TryGetProperty("avg_logprob", out var lp) ? lp.GetDouble() : 0;
            if (noSpeech > 0.6 && logProb < -1) continue; // Whisper's own "silent segment" rule
            text.Append(segment.GetProperty("text").GetString());
        }
        return text.ToString();
    }

    static string Clean(string text)
    {
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var lower = text.ToLowerInvariant();
        return lower.Length < 120 && Hallucinations.Any(lower.Contains) ? "" : text;
    }
}
