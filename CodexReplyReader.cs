using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SystemAudioTranscript;

internal static class CodexReplyReader
{
    public static async Task<string> WaitAsync(string receipt, Action openIfQueued, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var token = timeout.Token;
        var start = new ProcessStartInfo(CodexTranscriptDelivery.FindCodex())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("app-server");
        using var process = Process.Start(start) ?? throw new IOException("Không mở được trình đọc chat Codex.");
        process.BeginErrorReadLine();

        try
        {
            await CallAsync(1, "initialize", new
            {
                clientInfo = new { name = "system_audio_transcript", title = "System Audio Transcript", version = "1.0.0" },
            });
            await WriteAsync(new { method = "initialized" });

            var wait = Stopwatch.StartNew();
            var opened = false;
            var turnSeen = false;
            for (var id = 2; ; id++)
            {
                var result = await CallAsync(id, "thread/turns/list", new
                {
                    threadId = CodexTranscriptDelivery.ThreadId,
                    limit = 12,
                    sortDirection = "desc",
                    itemsView = "full",
                });
                foreach (var turn in result.GetProperty("data").EnumerateArray())
                {
                    if (!turn.GetProperty("items").EnumerateArray().Any(item => IsReceipt(item, receipt)))
                        continue;

                    turnSeen = true;
                    var status = turn.GetProperty("status").GetString();
                    if (status is "failed" or "interrupted")
                        throw new IOException("Codex đã nhận bản phiên âm nhưng lượt trả lời bị gián đoạn.");

                    var reply = turn.GetProperty("items").EnumerateArray()
                        .Where(item => item.GetProperty("type").GetString() == "agentMessage")
                        .LastOrDefault(item => item.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer");
                    if (reply.ValueKind != JsonValueKind.Undefined && reply.GetProperty("text").GetString() is { Length: > 0 } answer)
                        return answer;

                    if (status == "completed")
                    {
                        var lastMessage = turn.GetProperty("items").EnumerateArray()
                            .LastOrDefault(item => item.GetProperty("type").GetString() == "agentMessage");
                        if (lastMessage.ValueKind != JsonValueKind.Undefined &&
                            lastMessage.GetProperty("text").GetString() is { Length: > 0 } text)
                            return text;
                        throw new IOException("Lượt xử lý đã kết thúc mà Codex không trả về văn bản.");
                    }
                }

                if (!turnSeen && !opened && wait.Elapsed >= TimeSpan.FromSeconds(3))
                {
                    opened = true;
                    openIfQueued();
                }
                await Task.Delay(350, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex chưa có phản hồi. Bạn có thể kiểm tra lại mà không gửi trùng bản phiên âm.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Không đọc được định dạng phản hồi của Codex CLI hiện tại.", error);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
            }
        }

        async Task WriteAsync(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }

        async Task<JsonElement> CallAsync(int id, string method, object parameters)
        {
            await WriteAsync(new { id, method, @params = parameters });
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                    responseId.GetInt32() != id)
                    continue;

                if (message.TryGetProperty("error", out var error))
                    throw new IOException(error.GetProperty("message").GetString());
                return message.GetProperty("result").Clone();
            }
            throw new IOException("Codex CLI đã đóng kết nối đọc chat.");
        }
    }

    private static bool IsReceipt(JsonElement item, string receipt) =>
        item.GetProperty("type").GetString() == "userMessage" && item.GetProperty("content").EnumerateArray()
            .Any(content => content.TryGetProperty("text", out var text) &&
                text.GetString()?.Contains(receipt, StringComparison.Ordinal) == true);
}
