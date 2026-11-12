using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAudioTranscript;

internal static class GeminiReplyClient
{
    private const string Model = "gemini-3.5-flash-lite";
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions?alt=sse";
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<ApiReply> StreamAsync(string transcript, string? previousInteractionId, Action<string> showText,
        CancellationToken cancellationToken, bool detailed = false)
    {
        var timeoutSeconds = detailed ? 45 : 28;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                system_instruction = detailed ? InterviewContext.DetailedInstructions : InterviewContext.Instructions,
                input = previousInteractionId is null ? InterviewContext.FirstTurn(transcript) : transcript,
                previous_interaction_id = previousInteractionId,
                stream = true,
                store = true,
                generation_config = new { thinking_level = "minimal", max_output_tokens = detailed ? 640 : 256 },
            }, options: RequestJson),
        };
        request.Headers.Add("x-goog-api-key", ApiCredential.Gemini.Load());
        request.Headers.Add("Api-Revision", "2026-05-20");

        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new IOException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound when previousInteractionId is not null =>
                        "Gemini không tìm thấy lượt trước. Bấm Cuộc trò chuyện mới để bắt đầu lại với hồ sơ phỏng vấn.",
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"Gemini từ chối yêu cầu (HTTP {(int)response.StatusCode}). Kiểm tra API key và quyền truy cập trong Google AI Studio.",
                    HttpStatusCode.TooManyRequests => "Gemini đã chạm giới hạn sử dụng. Kiểm tra hạn mức trong Google AI Studio rồi thử lại.",
                    _ => $"Gemini API báo lỗi HTTP {(int)response.StatusCode}.",
                });

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadReplyAsync(stream, showText, token);
        }
        catch (HttpRequestException error)
        {
            throw new IOException("Không kết nối được Gemini API. Kiểm tra kết nối mạng rồi thử lại.", error);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Gemini trả về dữ liệu không đúng định dạng.", error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Gemini chưa hoàn tất phản hồi trong {timeoutSeconds} giây.");
        }
    }

    private static async Task<ApiReply> ReadReplyAsync(Stream stream, Action<string> showText, CancellationToken token)
    {
        var hasText = false;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(token))
        {
            if (item.Data == "[DONE]")
                break;

            using var document = JsonDocument.Parse(item.Data);
            var message = document.RootElement;
            switch (message.GetProperty("event_type").GetString())
            {
                case "step.delta" when message.TryGetProperty("delta", out var delta) &&
                    delta.GetProperty("type").GetString() == "text" &&
                    delta.GetProperty("text").GetString() is { Length: > 0 } text:
                    hasText |= !string.IsNullOrWhiteSpace(text);
                    showText(text);
                    break;
                case "interaction.completed":
                    var interaction = message.GetProperty("interaction");
                    if (hasText && interaction.GetProperty("status").GetString() == "completed" &&
                        interaction.GetProperty("id").GetString() is { Length: > 0 } id)
                        return new ApiReply(id, ApiReply.ReadTokens(interaction));
                    throw new IOException("Gemini kết thúc yêu cầu mà chưa trả về câu trả lời hoàn chỉnh.");
                case "error" or "interaction.failed":
                    throw new IOException("Gemini gặp lỗi trong khi tạo câu trả lời.");
            }
        }

        throw new IOException("Luồng trả lời của Gemini kết thúc trước khi hoàn tất.");
    }

}
