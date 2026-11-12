using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace SystemAudioTranscript;

internal static class WhisperModel
{
    private const string ModelName = "ggml-base.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    private const string ModelHash = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";
    private static readonly HttpClient Client = new();

    public static async Task<string> GetPathAsync(IProgress<string> status, CancellationToken stop)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemAudioTranscript", "models");
        var path = Path.Combine(folder, ModelName);
        if (File.Exists(path))
            return path;

        status.Report("Đang tải mô hình Whisper Base (148 MB)…");
        Directory.CreateDirectory(folder);
        var temporaryPath = Path.Combine(folder, $"{ModelName}.{Guid.NewGuid():N}.part");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            using var response = await Client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            await using (var model = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(model, token);
                model.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(model, token));
                if (hash != ModelHash)
                    throw new InvalidDataException("Tệp mô hình tải về không đúng mã kiểm tra SHA-256. Hãy thử lại.");
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance finished downloading the same model.
            }
            return path;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException("Tải mô hình quá thời gian chờ. Kiểm tra kết nối mạng rồi thử lại.");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
