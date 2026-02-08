using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HomelabBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services.Voice;

public sealed class OpenAiTextToSpeechService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<VoiceConfiguration> config,
    ILogger<OpenAiTextToSpeechService> logger)
    : ITextToSpeechService
{
    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var settings = config.CurrentValue;
            using var client = httpClientFactory.CreateClient("OpenAiAudio");

            var payload = JsonSerializer.Serialize(new
            {
                model = settings.TtsModel,
                input = text,
                voice = settings.TtsVoice,
                response_format = "pcm"
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to synthesize speech");
            return null;
        }
    }
}
