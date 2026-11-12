using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace SystemAudioTranscript;

internal sealed record OutputDevice(string Id, string Name, bool IsDefault)
{
    public string Label => IsDefault ? $"{Name} (mặc định)" : Name;
}

internal sealed record TranscriptLine(TimeSpan Start, string Text)
{
    public string Format() => $"[{(int)Start.TotalHours:00}:{Start.Minutes:00}:{Start.Seconds:00}] {Text}";
}

internal sealed record TranscriptUpdate(bool IsPreview, IReadOnlyList<TranscriptLine> Lines);

internal sealed class LoopbackTranscriber : IDisposable
{
    private WhisperFactory? factory;

    public static List<OutputDevice> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }

        var devices = new List<OutputDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
                devices.Add(new OutputDevice(endpoint.ID, endpoint.FriendlyName, endpoint.ID == defaultId));
        }

        return devices.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name).ToList();
    }

    public async Task RunAsync(string deviceId, string language, IProgress<string> status,
        IProgress<TranscriptUpdate> transcript, IProgress<int> droppedChunks, CancellationToken stop,
        CancellationToken abort)
    {
        if (factory is null)
        {
            var modelPath = await WhisperModel.GetPathAsync(status, stop);
            stop.ThrowIfCancellationRequested();
            status.Report("Đang nạp mô hình Whisper…");
            factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseFlashAttention = true });
        }

        await using var processor = CreateProcessor(factory, language);
        await using var shortProcessor = CreateProcessor(factory, language, 256);

        stop.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        await using var recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithFormat(new WaveFormat(LiveAudioBuffer.SampleRate, 16, 1))
            .WithBufferLength(30)
            .Build();

        using var buffer = new LiveAudioBuffer(droppedChunks);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Stopwatch();
        recorder.DataAvailable += (data, _, _, _) => buffer.AddPacket(data, clock.Elapsed);

        recorder.RecordingStopped += (_, args) =>
        {
            buffer.Complete();
            stopped.TrySetResult(args.Exception);
        };

        clock.Start();
        recorder.StartRecording();
        using var stopRegistration = stop.Register(recorder.StopRecording);
        status.Report("Đang nghe âm thanh từ thiết bị đã chọn…");

        var idleFlush = FlushIdleAsync();
        var processing = TranscribeAsync(buffer, processor, shortProcessor, transcript, abort, stop);
        try
        {
            if (await Task.WhenAny(stopped.Task, processing) == processing)
                recorder.StopRecording();

            var captureError = await stopped.Task;
            await processing;
            if (captureError is not null)
                throw new InvalidOperationException("Mất kết nối với thiết bị phát âm thanh.", captureError);
        }
        finally
        {
            timer.Dispose();
            await idleFlush;
        }

        async Task FlushIdleAsync()
        {
            while (await timer.WaitForNextTickAsync())
                buffer.FlushIdle(clock.Elapsed);
        }
    }

    public void Dispose() => factory?.Dispose();

    internal static WhisperProcessor CreateProcessor(WhisperFactory factory, string language, int audioContext = 512) => factory.CreateBuilder()
        .WithLanguage(language)
        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
        .WithAudioContextSize(audioContext)
        .WithNoContext()
        .Build();

    internal static async Task TranscribeAsync(LiveAudioBuffer buffer, WhisperProcessor processor, WhisperProcessor shortProcessor,
        IProgress<TranscriptUpdate> transcript, CancellationToken abort, CancellationToken stopPreviews = default)
    {
        using var previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(abort, stopPreviews);
        await foreach (var chunk in buffer.ReadAllAsync(abort))
        {
            var token = chunk.IsPreview ? previewCancellation.Token : abort;
            if (chunk.IsPreview && token.IsCancellationRequested)
                continue;

            var samples = new float[chunk.Pcm.Length / sizeof(short)];
            // The 256-frame context is 5.12 seconds, leaving headroom for clips up to four seconds.
            var activeProcessor = samples.Length <= 4 * LiveAudioBuffer.SampleRate ? shortProcessor : processor;
            var pcm = MemoryMarshal.Cast<byte, short>(chunk.Pcm);
            for (var i = 0; i < samples.Length; i++)
                samples[i] = pcm[i] / 32768f;

            var lines = new List<TranscriptLine>();
            try
            {
                await foreach (var segment in activeProcessor.ProcessAsync(samples, token))
                {
                    var text = segment.Text.Trim();
                    if (text.Length > 0)
                        lines.Add(new TranscriptLine(chunk.Start + segment.Start, text));
                }
            }
            catch (OperationCanceledException) when (chunk.IsPreview && stopPreviews.IsCancellationRequested && !abort.IsCancellationRequested)
            {
                continue;
            }
            if (!token.IsCancellationRequested)
                transcript.Report(new TranscriptUpdate(chunk.IsPreview, lines));
        }
    }
}
