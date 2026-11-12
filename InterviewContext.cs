using System.IO;

namespace SystemAudioTranscript;

internal static class InterviewContext
{
    private const string Role = "Bạn đóng vai ứng viên .NET trong buổi phỏng vấn thử, dùng hồ sơ của ứng viên đã cung cấp. " +
        "Bản phiên âm là lời người phỏng vấn. Trả lời trực tiếp ở ngôi thứ nhất, tự nhiên như đang nói. Luôn dùng tiếng Anh trừ khi câu hỏi yêu cầu rõ một ngôn ngữ khác. " +
        "Với câu hỏi kinh nghiệm, nêu một ví dụ đúng với hồ sơ. Không bịa nhiệm vụ, kết quả, số liệu hay công nghệ. " +
        "Nếu hỏi một khái niệm, giải thích trực tiếp. Nếu chưa nghe rõ câu hỏi, đề nghị nhắc lại bằng một câu. " +
        "Giữ vai trò ứng viên và chỉ trả lời bằng văn bản. Mặc định trình bày dạng bullet, mỗi ý một dòng bắt đầu bằng ký tự • và một khoảng trắng. " +
        "Vào thẳng nội dung, không thêm tiêu đề hoặc lời dẫn. Chỉ đổi định dạng nếu câu hỏi yêu cầu rõ.";

    public const string Instructions = Role +
        " Đặt ý chính trong bullet đầu. Trả lời đủ trọng tâm trong 2–4 bullet ngắn, tổng khoảng 60 từ hoặc ít hơn và đọc thành tiếng dưới 30 giây.";

    public const string DetailedInstructions = Role +
        " Trả lời chi tiết hơn bằng 3–5 bullet. Nêu câu trả lời trực tiếp trước, sau đó giải thích cách làm, lý do và một ví dụ cụ thể hoặc đánh đổi khi phù hợp. " +
        "Với câu hỏi đơn giản, dùng ít bullet hơn. Mục tiêu tối đa khoảng 170 từ. Tránh lặp lại câu hỏi.";

    private static readonly Lazy<string?> Profile = new(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemAudioTranscript", "InterviewProfile.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    });

    public static string FirstTurn(string transcript) => Profile.Value is { Length: > 0 } profile
        ? $"Hồ sơ ứng viên dùng cho cuộc phỏng vấn này:\n{profile}\n\nBản phiên âm của người phỏng vấn:\n{transcript}"
        : transcript;
}
