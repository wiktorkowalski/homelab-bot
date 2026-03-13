namespace HomelabBot.Services.Voice;

public interface ITextToSpeechService
{
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct = default);
}
