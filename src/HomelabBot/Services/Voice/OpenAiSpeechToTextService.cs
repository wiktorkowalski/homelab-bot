using System.Net.Http.Headers;
using System.Text.Json;
using HomelabBot.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services.Voice;

public sealed class OpenAiSpeechToTextService(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<VoiceConfiguration> config,
    ILogger<OpenAiSpeechToTextService> logger)
    : ISpeechToTextService
{
    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("OpenAiAudio");
            using var content = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(wavAudio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "audio.wav");
            content.Add(new StringContent("whisper-1"), "model");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.CurrentValue.OpenAiApiKey);
            request.Content = content;

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to transcribe audio");
            return null;
        }
    }
}
