using System.Collections.Concurrent;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using DSharpPlus.VoiceNext.EventArgs;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services.Voice;

public sealed class DiscordVoiceService : BackgroundService
{
    private readonly DiscordBotService _discordBot;
    private readonly KernelService _kernelService;
    private readonly ISpeechToTextService _sttService;
    private readonly ITextToSpeechService _ttsService;
    private readonly VoiceConfiguration _config;
    private readonly ILogger<DiscordVoiceService> _logger;

    private readonly ConcurrentDictionary<uint, UserAudioBuffer> _userBuffers = new();
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private VoiceNextConnection? _connection;
    private VoiceTransmitSink? _transmitSink;

    public DiscordVoiceService(
        DiscordBotService discordBot,
        KernelService kernelService,
        ISpeechToTextService sttService,
        ITextToSpeechService ttsService,
        IOptions<VoiceConfiguration> config,
        ILogger<DiscordVoiceService> logger)
    {
        _discordBot = discordBot;
        _kernelService = kernelService;
        _sttService = sttService;
        _ttsService = ttsService;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Voice service is disabled");
            return;
        }

        _logger.LogInformation("Voice service waiting for Discord bot to be ready...");
        await _discordBot.WaitForReadyAsync(stoppingToken);
        _logger.LogInformation("Voice service started");

        // Run buffer flush loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushExpiredBuffersAsync(stoppingToken);
                await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in voice service loop");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    public async Task JoinChannelAsync(DiscordChannel channel)
    {
        var client = _discordBot.Client;
        if (client == null)
            throw new InvalidOperationException("Discord client not connected");

        var vnext = client.GetVoiceNext();
        if (vnext == null)
            throw new InvalidOperationException("VoiceNext not configured");

        // Leave existing connection if any
        await LeaveChannelAsync();

        _connection = await vnext.ConnectAsync(channel);
        _transmitSink = _connection.GetTransmitSink();

        _connection.VoiceReceived += OnVoiceReceived;

        _logger.LogInformation("Joined voice channel: {Channel}", channel.Name);
    }

    public Task LeaveChannelAsync()
    {
        if (_connection != null)
        {
            _connection.VoiceReceived -= OnVoiceReceived;
            _connection.Disconnect();
            _connection = null;
            _transmitSink = null;
            _userBuffers.Clear();
            _logger.LogInformation("Left voice channel");
        }

        return Task.CompletedTask;
    }

    public VoiceStatus GetStatus()
    {
        return new VoiceStatus
        {
            IsConnected = _connection != null,
            ChannelName = _connection?.TargetChannel?.Name,
            ActiveUsers = _userBuffers.Count
        };
    }

    private Task OnVoiceReceived(VoiceNextConnection connection, VoiceReceiveEventArgs e)
    {
        if (e.AudioFormat.ChannelCount == 0)
            return Task.CompletedTask;

        var buffer = _userBuffers.GetOrAdd(e.SSRC, _ => new UserAudioBuffer(_config));
        buffer.AppendPcm(e.PcmData.ToArray());

        return Task.CompletedTask;
    }

    private async Task FlushExpiredBuffersAsync(CancellationToken ct)
    {
        foreach (var (ssrc, buffer) in _userBuffers)
        {
            if (!buffer.ShouldFlush())
                continue;

            var pcmData = buffer.Flush();
            if (pcmData == null || pcmData.Length == 0)
                continue;

            // Check minimum length
            var durationMs = pcmData.Length / (48000 * 2 * 2 / 1000); // 48kHz stereo 16-bit
            if (durationMs < _config.MinAudioLengthMs)
            {
                _logger.LogDebug("Audio too short ({Duration}ms), discarding", durationMs);
                continue;
            }

            if (durationMs > _config.MaxAudioLengthMs)
            {
                _logger.LogWarning("Audio too long ({Duration}ms), discarding", durationMs);
                continue;
            }

            _ = ProcessAudioAsync(ssrc, pcmData, ct);
        }
    }

    private async Task ProcessAudioAsync(uint ssrc, byte[] pcmData, CancellationToken ct)
    {
        try
        {
            // Convert to WAV for Whisper (48kHz stereo from Discord)
            var wavData = AudioConverter.PcmToWav(pcmData, 48000, 2);

            // Transcribe
            var text = await _sttService.TranscribeAsync(wavData, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Empty transcription from SSRC {Ssrc}", ssrc);
                return;
            }

            _logger.LogInformation("Voice transcription from SSRC {Ssrc}: {Text}", ssrc, text);

            // Process with kernel (use SSRC as pseudo-thread-id)
            var response = await _kernelService.ProcessMessageAsync(
                ssrc,
                text,
                0, // No Discord user ID available from SSRC
                TraceType.Chat,
                ct);

            _logger.LogInformation("Voice response: {Response}", response);

            // Synthesize and play
            await SynthesizeAndPlayAsync(response, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing voice audio from SSRC {Ssrc}", ssrc);
        }
    }

    private async Task SynthesizeAndPlayAsync(string text, CancellationToken ct)
    {
        if (_transmitSink == null || _connection == null)
            return;

        var pcm24k = await _ttsService.SynthesizeAsync(text, ct);
        if (pcm24k == null || pcm24k.Length == 0)
            return;

        var pcm48kStereo = AudioConverter.ResamplePcm24kMonoTo48kStereo(pcm24k);

        await _playbackLock.WaitAsync(ct);
        try
        {
            await _transmitSink.WriteAsync(new ReadOnlyMemory<byte>(pcm48kStereo), ct);
            await _transmitSink.FlushAsync(ct);
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    private sealed class UserAudioBuffer
    {
        private readonly List<byte> _pcmData = [];
        private readonly VoiceConfiguration _config;
        private readonly object _lock = new();
        private DateTime _lastReceived = DateTime.UtcNow;

        public UserAudioBuffer(VoiceConfiguration config)
        {
            _config = config;
        }

        public void AppendPcm(byte[] data)
        {
            lock (_lock)
            {
                if (AudioConverter.IsSilence(data))
                    return;

                _pcmData.AddRange(data);
                _lastReceived = DateTime.UtcNow;
            }
        }

        public bool ShouldFlush()
        {
            lock (_lock)
            {
                if (_pcmData.Count == 0)
                    return false;

                var silenceMs = (DateTime.UtcNow - _lastReceived).TotalMilliseconds;
                return silenceMs >= _config.SilenceThresholdMs;
            }
        }

        public byte[]? Flush()
        {
            lock (_lock)
            {
                if (_pcmData.Count == 0)
                    return null;

                var data = _pcmData.ToArray();
                _pcmData.Clear();
                return data;
            }
        }
    }

    public sealed class VoiceStatus
    {
        public bool IsConnected { get; init; }
        public string? ChannelName { get; init; }
        public int ActiveUsers { get; init; }
    }
}
