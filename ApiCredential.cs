using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SystemAudioTranscript;

internal sealed class ApiCredential(string provider, string environmentVariable, string fileName)
{
    public static ApiCredential Gemini { get; } = new("Gemini", "GEMINI_API_KEY", "gemini-key.dpapi");
    public static ApiCredential Zai { get; } = new("Z.AI", "ZAI_API_KEY", "zai-key.dpapi");

    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemAudioTranscript", fileName);

    public string Load()
    {
        var configured = ReadEnvironment();
        try
        {
            return ReadSaved() ?? configured ??
                throw new IOException($"Chưa có API key {provider}. Bấm API key trong ứng dụng để nhập và lưu.");
        }
        catch (Exception error) when (configured is not null && error is IOException or UnauthorizedAccessException)
        {
            return configured;
        }
    }

    public string Describe()
    {
        var configured = ReadEnvironment() is not null ? $"Đang dùng {environmentVariable}" : null;
        try
        {
            if (ReadSaved() is not null)
                return "Đã lưu trên tài khoản Windows này";
        }
        catch (IOException)
        {
            return configured ?? "Windows không đọc được key đã lưu. Hãy nhập lại.";
        }
        catch (UnauthorizedAccessException)
        {
            return configured ?? "Windows không cho phép đọc key đã lưu.";
        }

        return configured ?? "Chưa cấu hình";
    }

    public void Validate(string value)
    {
        if (value.Length == 0 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new IOException($"API key {provider} phải nằm trên một dòng, không có khoảng trắng bên trong.");
    }

    public void Save(string value)
    {
        Validate(value);
        var clear = Encoding.UTF8.GetBytes(value);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string? ReadEnvironment() =>
        Environment.GetEnvironmentVariable(environmentVariable)?.Trim() is { Length: > 0 } value ? value : null;

    private string? ReadSaved()
    {
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var value = Encoding.UTF8.GetString(clear).Trim();
                return value.Length > 0 ? value : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (CryptographicException error)
        {
            throw new IOException($"Windows không đọc được API key {provider} đã lưu. Bấm API key để nhập lại.", error);
        }
    }
}
