using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SystemAudioTranscript;

public partial class ApiUsageWindow : Window
{
    public ApiUsageWindow()
    {
        InitializeComponent();
        try
        {
            var state = ApiUsageStore.Read();
            ShowUsage(state);
            GeminiBudget.Text = state.Gemini.Budget?.ToString(CultureInfo.CurrentCulture) ?? "";
            ZaiBudget.Text = state.Zai.Budget?.ToString(CultureInfo.CurrentCulture) ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Windows không đọc được thống kê API.";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowCaptureExclusion.Apply(this);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadBudget(GeminiBudget.Text, out var gemini) || !TryReadBudget(ZaiBudget.Text, out var zai))
        {
            Status.Text = "Nhập số nguyên dương hoặc để trống nếu không đặt ngân sách.";
            return;
        }

        try
        {
            ApiUsageStore.SetBudgets(gemini, zai);
            ShowUsage(ApiUsageStore.Read());
            Status.Text = "Đã lưu ngân sách.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "Windows không lưu được ngân sách API.";
        }
    }

    private void ShowUsage(ApiUsageDay day)
    {
        GeminiUsage.Text = Format(day.Gemini);
        ZaiUsage.Text = Format(day.Zai);
    }

    private static string Format(ApiMeter meter)
    {
        var remaining = meter.Budget is null ? "Chưa đặt ngân sách"
            : meter.Remaining is long balance ? $"Còn {balance:N0} token trong ngân sách"
            : "Thiếu số liệu để tính phần còn lại";
        var missing = meter.MissingUsage > 0 ? $" · {meter.MissingUsage:N0} lượt thiếu số token" : "";
        return $"Đã ghi nhận {meter.Tokens:N0} token · {meter.Replies:N0} lượt{missing}\n{remaining}";
    }

    private static bool TryReadBudget(string input, out long? budget)
    {
        budget = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;
        if (!long.TryParse(input, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) || value <= 0)
            return false;
        budget = value;
        return true;
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            Status.Text = "Không mở được trình duyệt để xem tài khoản API.";
        }
    }
}
