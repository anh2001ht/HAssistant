# HAssistant

Ứng dụng WPF cho Windows lấy âm thanh từ loa hoặc tai nghe bằng WASAPI loopback và phiên âm cục bộ bằng Whisper. Sau khi dừng, ứng dụng gửi bản phiên âm đến Gemini, Z.AI hoặc chat Codex đã cấu hình. Gemini có hai ô trả lời song song, một ô ngắn gọn và một ô chi tiết.

## Chạy từ mã nguồn

Cần Windows 10 phiên bản 2004 trở lên hoặc Windows 11 và .NET 10 SDK. Chạy từ thư mục repo:

```powershell
dotnet run --project .\SystemAudioTranscript.csproj
```

1. Mở **Thiết lập** → **API key** để lưu key Gemini hoặc Z.AI. Ứng dụng mã hóa key bằng Windows DPAPI theo tài khoản hiện tại. Cũng có thể dùng biến môi trường `GEMINI_API_KEY` hoặc `ZAI_API_KEY`.
2. Chọn thiết bị phát và ngôn ngữ lời nói. Whisper mặc định nghe tiếng Anh. Lần đầu cần Internet để tải mô hình khoảng 148 MB.
3. Bấm **Bắt đầu nghe** hoặc phím tắt. Bấm lần nữa để chốt và gửi. Có thể đổi tổ hợp trong **Phím tắt** và bật **Dùng chung phím tắt** trong **Thiết lập**.
4. Dùng **Ghim** để giữ cửa sổ nổi và mở **Bản phiên âm** để xem hoặc lưu `.txt`.

## Dung lượng API

Trong **Thiết lập** → **Dung lượng API**, xem tổng token do API báo cho các phản hồi hoàn tất trong ngày. Đặt ngân sách token/ngày riêng cho Gemini và Z.AI để xem phần còn lại theo bộ đếm của ứng dụng. Gemini ngắn gọn và chi tiết được tính chung. Bộ đếm dùng ngày theo giờ Windows và chỉ gồm các yêu cầu từ ứng dụng này.

Hạn mức hoặc số dư thực tế có thể xem tại [Google AI Studio](https://aistudio.google.com/rate-limit) và [Z.AI Billing](https://z.ai/manage-apikey/billing).

## Cấu hình cá nhân tùy chọn

Thư mục cấu hình là `%LOCALAPPDATA%\SystemAudioTranscript`.

- Đặt hồ sơ kinh nghiệm UTF-8 trong `InterviewProfile.txt` nếu muốn ứng dụng dùng hồ sơ cho cuộc phỏng vấn mới.
- Để dùng chế độ Codex, cài Codex CLI và tạo `codex-thread.json` với hai trường `ThreadId` và `Title`. `ThreadId` là UUID của chat Codex cục bộ muốn tiếp tục. Mở lại ứng dụng sau khi đổi cấu hình.
- API key, hồ sơ, chat đích, thống kê và phím tắt được giữ trong thư mục cấu hình Windows.

Cửa sổ chính yêu cầu `WDA_EXCLUDEFROMCAPTURE`. Hiệu lực phụ thuộc vào phương thức chụp hoặc chia sẻ màn hình. [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
