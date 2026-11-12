using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace SystemAudioTranscript;

internal sealed record AudioChunk(TimeSpan Start, byte[] Pcm, bool IsPreview);

internal sealed class LiveAudioBuffer(IProgress<int> droppedChunks) : IDisposable
{
    public const int SampleRate = 16_000;
    private const int BytesPerSecond = SampleRate * sizeof(short);
    private const int PreviewInterval = BytesPerSecond * 12 / 10;
    private const int MaxChunkLength = BytesPerSecond * 8;
    private const int PauseLength = BytesPerSecond * 4 / 10;
    private const int QuietPeak = 32;
    private const int MaxPendingChunks = 4;
    private static readonly TimeSpan PacketTimeout = TimeSpan.FromMilliseconds(350);

    private readonly object gate = new();
    private readonly MemoryStream pcm = new();
    private readonly Queue<AudioChunk> completedChunks = new();
    private readonly Channel<bool> workAvailable = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = true,
    });

    private AudioChunk? preview;
    private TimeSpan chunkStart;
    private TimeSpan lastPacket;
    private int nextPreview = PreviewInterval;
    private int quietBytes;
    private int dropped;

    public void AddPacket(ReadOnlySpan<byte> data, TimeSpan now)
    {
        if (data.IsEmpty)
            return;

        var audible = HasSignal(data);
        lock (gate)
        {
            if (now - lastPacket >= PacketTimeout)
                FinishChunk();
            lastPacket = now;

            if (pcm.Length == 0)
            {
                if (!audible)
                    return;
                var packetDuration = TimeSpan.FromSeconds(data.Length / (double)BytesPerSecond);
                chunkStart = now > packetDuration ? now - packetDuration : TimeSpan.Zero;
            }

            pcm.Write(data);
            quietBytes = audible ? 0 : quietBytes + data.Length;
            if (pcm.Length >= MaxChunkLength || quietBytes >= PauseLength)
            {
                FinishChunk();
            }
            else if (audible && pcm.Length >= nextPreview && completedChunks.Count == 0)
            {
                preview = new AudioChunk(chunkStart, pcm.ToArray(), IsPreview: true);
                nextPreview = (int)pcm.Length + PreviewInterval;
                workAvailable.Writer.TryWrite(true);
            }
        }
    }

    public void FlushIdle(TimeSpan now)
    {
        // WASAPI can stop delivering packets entirely when playback stops.
        lock (gate)
        {
            if (now - lastPacket >= PacketTimeout)
                FinishChunk();
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            FinishChunk();
            workAvailable.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<AudioChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken abort)
    {
        await foreach (var _ in workAvailable.Reader.ReadAllAsync(abort))
        {
            while (TakeNext() is { } chunk)
            {
                abort.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    public void Dispose() => pcm.Dispose();

    // Caller holds gate. Final chunks take priority, and the latest preview replaces any pending preview.
    private void FinishChunk()
    {
        if (pcm.Length == 0)
            return;

        preview = null;
        if (completedChunks.Count == MaxPendingChunks)
        {
            completedChunks.Dequeue();
            droppedChunks.Report(++dropped);
        }
        completedChunks.Enqueue(new AudioChunk(chunkStart, pcm.ToArray(), IsPreview: false));
        pcm.SetLength(0);
        quietBytes = 0;
        nextPreview = PreviewInterval;
        workAvailable.Writer.TryWrite(true);
    }

    private AudioChunk? TakeNext()
    {
        lock (gate)
        {
            if (completedChunks.TryDequeue(out var chunk))
                return chunk;

            var pendingPreview = preview;
            preview = null;
            return pendingPreview;
        }
    }

    private static bool HasSignal(ReadOnlySpan<byte> data)
    {
        foreach (var sample in MemoryMarshal.Cast<byte, short>(data))
        {
            if (sample > QuietPeak || sample < -QuietPeak)
                return true;
        }
        return false;
    }
}
