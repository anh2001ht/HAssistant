using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace SystemAudioTranscript;

public partial class ApiKeysWindow : Window
{
    public bool GeminiSaved { get; private set; }
    public bool ZaiSaved { get; private set; }

    public ApiKeysWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (WindowCaptureExclusion.Apply(this) is int error)
        {
            CaptureWarning.Text = $"Windows chưa áp dụng được chế độ loại khỏi chụp màn hình (mã {error}).";
            CaptureWarning.Visibility = Visibility.Visible;
        }
    }

    private void RefreshStatus()
    {
        GeminiStatus.Text = ApiCredential.Gemini.Describe();
        ZaiStatus.Text = ApiCredential.Zai.Describe();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var gemini = GeminiKey.Password.Trim();
        var zai = ZaiKey.Password.Trim();
        if (gemini.Length == 0 && zai.Length == 0)
        {
            SaveError.Text = "Nhập ít nhất một API key để lưu.";
            return;
        }

        try
        {
            if (gemini.Length > 0)
                ApiCredential.Gemini.Validate(gemini);
            if (zai.Length > 0)
                ApiCredential.Zai.Validate(zai);

            if (gemini.Length > 0)
            {
                ApiCredential.Gemini.Save(gemini);
                GeminiSaved = true;
                GeminiKey.Clear();
            }
            if (zai.Length > 0)
            {
                ApiCredential.Zai.Save(zai);
                ZaiSaved = true;
                ZaiKey.Clear();
            }
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            RefreshStatus();
            SaveError.Text = $"Chưa lưu xong API key. {error.Message}";
        }
    }
}
