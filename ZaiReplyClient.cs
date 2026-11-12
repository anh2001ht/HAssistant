using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

namespace SystemAudioTranscript;

internal sealed record ZaiMessage(string Role, string Content);

internal static class ZaiReplyClient
{
    private const string Model = "glm-4.5-flash";
    private const string Endpoint = "https://api.z.ai/api/paas/v4/chat/completions";
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public static async Task<ApiReply> StreamAsync(ZaiMessage question, IReadOnlyList<ZaiMessage> history,
        Action<string> showText, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(28));
        var token = timeout.Token;
        ZaiMessage[] messages = [new("system", InterviewContext.Instructions), .. history, question];
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                messages,
                stream = true,
                thinking = new { type = "disabled" },
                max_tokens = 256,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiCredential.Zai.Load());

        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new IOException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"Z.AI từ chối truy cập (HTTP {(int)response.StatusCode}). Kiểm tra key và quyền của model.",
                    HttpStatusCode.TooManyRequests => "Z.AI đang quá tải hoặc đã chạm hạn mức (HTTP 429). Hãy thử lại.",
                    _ => $"Z.AI API báo lỗi HTTP {(int)response.StatusCode}.",
                });

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadReplyAsync(stream, showText, token);
        }
        catch (HttpRequestException error)
        {
            throw new IOException("Không kết nối được Z.AI API. Kiểm tra kết nối mạng rồi thử lại.", error);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Z.AI trả về dữ liệu không đúng định dạng.", error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Z.AI chưa hoàn tất phản hồi trong 28 giây.");
        }
    }

    private static async Task<ApiReply> ReadReplyAsync(Stream stream, Action<string> showText, CancellationToken token)
    {
        var answer = new StringBuilder();
        string? finishReason = null;
        long? totalTokens = null;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(token))
        {
            if (item.Data == "[DONE]")
                break;

            using var document = JsonDocument.Parse(item.Data);
            var message = document.RootElement;
            if (message.TryGetProperty("error", out _))
                throw new IOException("Z.AI gặp lỗi trong khi tạo câu trả lời.");

            totalTokens = ApiReply.ReadTokens(message) ?? totalTokens;
            if (!message.TryGetProperty("choices", out var choices))
                continue;

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text)
                {
                    answer.Append(text);
                    showText(text);
                }

                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
            }
        }

        var result = answer.ToString().Trim();
        return finishReason == "stop" && result.Length > 0
            ? new ApiReply(result, totalTokens)
            : throw new IOException("Luồng Z.AI kết thúc trước khi trả lời hoàn chỉnh.");
    }
}
