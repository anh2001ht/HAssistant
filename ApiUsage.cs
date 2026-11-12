using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAudioTranscript;

internal enum ApiProvider { Gemini, Zai }

internal sealed record ApiReply(string Value, long? Tokens)
{
    public static long? ReadTokens(JsonElement response) =>
        response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty("total_tokens", out var total) && total.ValueKind == JsonValueKind.Number &&
        total.TryGetInt64(out var count) && count >= 0 ? count : null;
}

internal sealed record ApiMeter(long? Budget = null, long Tokens = 0, long Replies = 0, long MissingUsage = 0)
{
    [JsonIgnore]
    public long? Remaining => Budget is long budget && MissingUsage == 0 ? Math.Max(0, budget - Tokens) : null;

    public ApiMeter Add(long? tokens) => this with
    {
        Replies = Replies + 1,
        Tokens = Tokens + (tokens ?? 0),
        MissingUsage = MissingUsage + (tokens is null ? 1 : 0),
    };
}

internal sealed record ApiUsageDay(DateOnly Date, ApiMeter Gemini, ApiMeter Zai);

internal static class ApiUsageStore
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemAudioTranscript", "api-usage.json");

    public static ApiUsageDay Read()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            if (JsonSerializer.Deserialize<ApiUsageDay>(File.ReadAllText(PathName)) is { Gemini: not null, Zai: not null } state)
                return state.Date == today ? state : new ApiUsageDay(today, new(state.Gemini.Budget), new(state.Zai.Budget));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
        }
        return new ApiUsageDay(today, new(), new());
    }

    public static void SetBudgets(long? gemini, long? zai) => Update(state => state with
    {
        Gemini = state.Gemini with { Budget = gemini },
        Zai = state.Zai with { Budget = zai },
    });

    public static ApiMeter? Record(ApiProvider provider, long? tokens)
    {
        try
        {
            var updated = Update(state => provider == ApiProvider.Gemini
                ? state with { Gemini = state.Gemini.Add(tokens) }
                : state with { Zai = state.Zai.Add(tokens) });
            return provider == ApiProvider.Gemini ? updated.Gemini : updated.Zai;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ApiUsageDay Update(Func<ApiUsageDay, ApiUsageDay> change)
    {
        using var mutex = new Mutex(false, @"Local\SystemAudioTranscript.ApiUsage");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                throw new IOException("Thống kê API đang được cửa sổ khác cập nhật. Hãy thử lại.");
        }
        catch (AbandonedMutexException)
        {
            // A previous instance exited mid-write. The published file is replaced atomically.
        }

        try
        {
            var state = change(Read());
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var temporary = $"{PathName}.{Guid.NewGuid():N}.part";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(state));
                File.Move(temporary, PathName, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            return state;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
