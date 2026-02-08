namespace HomelabBot.Services.Voice;

public interface ISpeechToTextService
{
    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default);
}
