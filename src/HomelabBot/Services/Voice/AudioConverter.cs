namespace HomelabBot.Services.Voice;

public static class AudioConverter
{
    /// <summary>Creates a WAV file from raw PCM data with proper RIFF header.</summary>
    public static byte[] PcmToWav(byte[] pcm, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var ms = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);

        return ms.ToArray();
    }

    /// <summary>Resamples PCM from 24kHz mono 16-bit to 48kHz stereo 16-bit.</summary>
    public static byte[] ResamplePcm24kMonoTo48kStereo(byte[] input)
    {
        int sampleCount = input.Length / 2;
        var output = new byte[sampleCount * 8];

        for (int i = 0; i < sampleCount; i++)
        {
            byte lo = input[i * 2];
            byte hi = input[(i * 2) + 1];

            int outBase = i * 8;
            output[outBase] = lo;
            output[outBase + 1] = hi;
            output[outBase + 2] = lo;
            output[outBase + 3] = hi;
            output[outBase + 4] = lo;
            output[outBase + 5] = hi;
            output[outBase + 6] = lo;
            output[outBase + 7] = hi;
        }

        return output;
    }

    /// <summary>Energy-based silence detection on 16-bit PCM.</summary>
    public static bool IsSilence(ReadOnlySpan<byte> pcm16, short threshold = 500)
    {
        if (pcm16.Length < 2)
            return true;

        long energy = 0;
        int sampleCount = pcm16.Length / 2;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[(i * 2) + 1] << 8));
            energy += Math.Abs(sample);
        }

        return energy / sampleCount < threshold;
    }
}
