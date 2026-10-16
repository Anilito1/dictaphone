using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dictee;

internal sealed record CapturedAudio(byte[] Wav, int SpeechMs);

internal sealed class DictationException(string message) : Exception(message);

/// <summary>One microphone capture (16 kHz mono PCM via winmm), recorded on a dedicated thread.</summary>
internal sealed unsafe class Recording
{
    const int SampleRate = 16000;
    const int ChunkMs = 50;
    const int ChunkBytes = SampleRate * 2 * ChunkMs / 1000;
    const int ChunkCount = 10;
    const double SpeechDb = -50;

    readonly AutoResetEvent signal = new(false);
    readonly MemoryStream pcm = new(SampleRate * 2 * 10);
    readonly Thread thread;
    volatile bool stopping;
    string? error;
    int speechMs;

    /// <summary>Current input loudness, 0 (silence) to 1 (loud).</summary>
    public float Level { get; private set; }

    Recording()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Dictee audio", Priority = ThreadPriority.AboveNormal };
    }

    public static Recording Start()
    {
        var recording = new Recording();
        recording.thread.Start();
        return recording;
    }

    public Task<CapturedAudio> StopAsync() => Task.Run(() =>
    {
        Finish();
        if (error != null) throw new DictationException(error);
        return new CapturedAudio(EncodeWav(pcm.GetBuffer().AsSpan(0, (int)pcm.Length)), speechMs);
    });

    public void Cancel() => Task.Run(Finish);

    void Finish()
    {
        stopping = true;
        signal.Set();
        thread.Join();
        signal.Dispose();
        Level = 0;
    }

    void Run()
    {
        var format = new Native.WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * 2,
            nBlockAlign = 2,
            wBitsPerSample = 16,
        };

        int result = Native.waveInOpen(out var device, Native.WAVE_MAPPER, ref format, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, Native.CALLBACK_EVENT);
        if (result != 0)
        {
            error = "Micro indisponible";
            Log.Write($"waveInOpen failed: {result}");
            return;
        }

        int headerSize = sizeof(Native.WAVEHDR);
        var headers = (Native.WAVEHDR*)NativeMemory.AllocZeroed((nuint)(headerSize * ChunkCount));
        var buffers = (byte*)NativeMemory.Alloc(ChunkBytes * ChunkCount);
        try
        {
            for (int i = 0; i < ChunkCount; i++)
            {
                headers[i].lpData = (IntPtr)(buffers + i * ChunkBytes);
                headers[i].dwBufferLength = ChunkBytes;
                Native.waveInPrepareHeader(device, &headers[i], headerSize);
                Native.waveInAddBuffer(device, &headers[i], headerSize);
            }
            Native.waveInStart(device);

            int next = 0;
            while (!stopping)
            {
                signal.WaitOne(100);
                next = Drain(device, headers, next, requeue: true);
            }

            Native.waveInReset(device);
            Drain(device, headers, next, requeue: false);
            for (int i = 0; i < ChunkCount; i++) Native.waveInUnprepareHeader(device, &headers[i], headerSize);
        }
        finally
        {
            Native.waveInClose(device);
            NativeMemory.Free(headers);
            NativeMemory.Free(buffers);
        }
    }

    /// <summary>Consumes completed buffers in capture order, starting at <paramref name="next"/>.</summary>
    int Drain(IntPtr device, Native.WAVEHDR* headers, int next, bool requeue)
    {
        for (int n = 0; n < ChunkCount; n++)
        {
            var header = &headers[next];
            if ((header->dwFlags & Native.WHDR_DONE) == 0) break;

            Consume((byte*)header->lpData, (int)header->dwBytesRecorded);
            if (requeue)
            {
                header->dwFlags &= ~Native.WHDR_DONE;
                Native.waveInAddBuffer(device, header, sizeof(Native.WAVEHDR));
            }
            next = (next + 1) % ChunkCount;
        }
        return next;
    }

    void Consume(byte* data, int count)
    {
        if (count < 2) return;
        pcm.Write(new ReadOnlySpan<byte>(data, count));

        var samples = new ReadOnlySpan<short>(data, count / 2);
        double sum = 0;
        foreach (var s in samples) sum += s * (double)s;
        double db = 20 * Math.Log10(Math.Sqrt(sum / samples.Length) / 32768.0 + 1e-9);

        if (db > SpeechDb) speechMs += count * 1000 / (SampleRate * 2);
        Level = (float)Math.Clamp((db + 55) / 40, 0, 1);
    }

    static byte[] EncodeWav(ReadOnlySpan<byte> samples)
    {
        var wav = new byte[44 + samples.Length];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + samples.Length);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], samples.Length);
        samples.CopyTo(s[44..]);
        return wav;
    }
}
