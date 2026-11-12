using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SystemAudioTranscript;

public partial class MainWindow : Window
{
    private readonly LoopbackTranscriber transcriber = new();
    private CaptureShortcut shortcut = CaptureShortcut.Load();
    private CaptureHotkey? captureHotkey;
    private bool choosingShortcut;
    private bool shortcutRegistered;
    private CancellationTokenSource? stop;
    private CancellationTokenSource? abort;
    private Task? session;
    private CodexTranscriptDelivery? pendingDelivery;
    private string? pendingGemini;
    private string? pendingDetail;
    private string? pendingZai;
    private readonly List<ZaiMessage> zaiHistory = [];
    private string? geminiInteractionId;
    private string? detailInteractionId;
    private string? awaitingReply;
    private CancellationTokenSource? replyWait;
    private bool sendOnStop;
    private long? stopRequestedAt;
    private bool sending;
    private bool closing;

    [Flags]
    private enum ReplyTarget { Primary = 1, Detail = 2, Zai = 4, All = Primary | Detail | Zai }

    public MainWindow()
    {
        InitializeComponent();
        SharedShortcutBox.IsChecked = shortcut.Shared;
    }

    protected override void OnClosed(EventArgs e)
    {
        captureHotkey?.Dispose();
        transcriber.Dispose();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (WindowCaptureExclusion.Apply(this) is int error)
        {
            CaptureWarning.Text = $"Windows chưa áp dụng được chế độ loại khỏi chụp màn hình (mã {error}).";
            CaptureWarning.Visibility = Visibility.Visible;
        }
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            captureHotkey = new CaptureHotkey(source);
            captureHotkey.Pressed += OnShortcutPressed;
            shortcutRegistered = captureHotkey.TryRegister(shortcut);
        }
        EndShortcutSelection();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => LoadOutputDevices();

    private void ApiUsage_Click(object sender, RoutedEventArgs e) => new ApiUsageWindow { Owner = this }.ShowDialog();

    private void ApiKeys_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ApiKeysWindow { Owner = this };
        dialog.ShowDialog();

        if (dialog.GeminiSaved)
        {
            geminiInteractionId = null;
            detailInteractionId = null;
        }
        if (dialog.ZaiSaved)
            zaiHistory.Clear();
        if (dialog.GeminiSaved || dialog.ZaiSaved)
            Status.Text = pendingGemini is not null || pendingDetail is not null || pendingZai is not null
                ? "Đã lưu API key. Bấm Gửi lại ở ô phản hồi cần thử lại."
                : "Đã lưu API key trên tài khoản Windows này.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadOutputDevices();

    private void LoadOutputDevices()
    {
        var previous = (Devices.SelectedItem as OutputDevice)?.Id;
        try
        {
            var devices = LoopbackTranscriber.GetOutputDevices();
            Devices.ItemsSource = devices;
            Devices.SelectedItem = devices.FirstOrDefault(device => device.Id == previous) ?? devices.FirstOrDefault();
            Status.Text = devices.Count == 0
                ? "Chưa thấy thiết bị phát. Kết nối loa hoặc tai nghe, rồi bấm Làm mới."
                : ReadyStatus();
        }
        catch (Exception error)
        {
            Devices.ItemsSource = null;
            Status.Text = $"Không đọc được thiết bị phát. {error.Message}";
        }
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e) => UpdateCaptureButton();

    private void UpdateCaptureButton()
    {
        if (CaptureButton is null)
            return;

        var finishing = sending || (stop is not null && sendOnStop);
        CaptureButton.Content = finishing ? "Đang xử lý…" : stop is null ? "Bắt đầu nghe" : "Dừng và gửi";
        CaptureButton.IsEnabled = !closing && !finishing && (stop is not null || Devices.SelectedItem is OutputDevice);
    }

    private void ShowShortcut() => ShortcutButton.Content = $"Phím tắt: {shortcut}";

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        choosingShortcut = !choosingShortcut;
        if (!choosingShortcut)
        {
            EndShortcutSelection();
            return;
        }

        ShortcutButton.Focus();
        ShortcutButton.Content = "Nhấn phím mới…";
        ShortcutStatus.Text = "Chọn phím F hoặc tổ hợp Ctrl, Alt, Shift. Nhấn Esc để hủy.";
    }

    private void Shortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (!choosingShortcut)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (e.IsRepeat || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndShortcutSelection();
            return;
        }

        var candidate = new CaptureShortcut(key, Keyboard.Modifiers, shortcut.Shared);
        if (!candidate.IsSupported)
        {
            ShortcutStatus.Text = "Chọn phím F (trừ F12) hoặc tổ hợp có Ctrl, Alt hay Shift.";
            return;
        }
        ApplyShortcut(candidate);
    }

    private void SharedShortcut_Click(object sender, RoutedEventArgs e)
    {
        ApplyShortcut(shortcut with { Shared = SharedShortcutBox.IsChecked == true });
        SharedShortcutBox.IsChecked = shortcut.Shared;
    }

    private void ApplyShortcut(CaptureShortcut candidate)
    {
        if (captureHotkey?.TryRegister(candidate) != true)
        {
            ShortcutStatus.Text = $"Windows không đăng ký được {candidate}. Hãy chọn phím khác.";
            return;
        }

        shortcut = candidate;
        shortcutRegistered = true;
        EndShortcutSelection();
        try
        {
            shortcut.Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShortcutStatus.Text = "Phím tắt đã đổi cho phiên này. Windows chưa lưu được cho lần mở sau.";
        }
    }

    private void Shortcut_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (choosingShortcut)
            EndShortcutSelection();
    }

    private void EndShortcutSelection()
    {
        choosingShortcut = false;
        ShowShortcut();
        ShortcutStatus.Text = !shortcutRegistered
            ? $"Phím {shortcut} chưa đăng ký được. Bấm Phím tắt để chọn phím khác."
            : captureHotkey?.IsShared == true && !shortcut.Shared
                ? $"{shortcut} đang được dùng chung với ứng dụng khác." : "";
    }

    private async void OnShortcutPressed()
    {
        if (choosingShortcut)
        {
            EndShortcutSelection();
            return;
        }

        await ToggleCaptureAsync();
    }

    private bool UsesCodex => ReplyProvider.SelectedValue is "codex";
    private bool UsesComparison => ReplyProvider.SelectedValue is "compare";

    private string ReadyStatus() => UsesCodex
        ? $"Sẵn sàng. Dừng và gửi sẽ chuyển bản phiên âm vào “{CodexTranscriptDelivery.ThreadTitle}”."
        : "";

    private void ReplyProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        NewConversationButton.Visibility = UsesCodex ? Visibility.Collapsed : Visibility.Visible;
        UpdateReplyLayout();
        if (Transcript.Text.Length > 0)
            return;

        UpdateReplyHeading();
        if (Devices.SelectedItem is OutputDevice)
            Status.Text = ReadyStatus();
    }

    private void UpdateReplyHeading()
    {
        ReplyHeading.Text = UsesCodex ? "Codex" : "Gemini · Ngắn gọn";
        OpenChatButton.Visibility = UsesCodex ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplyLayout();
    }

    private void UpdateReplyLayout()
    {
        DetailPane.Visibility = UsesCodex ? Visibility.Collapsed : Visibility.Visible;
        DetailColumn.Width = UsesCodex ? new GridLength(0) : new GridLength(1.2, GridUnitType.Star);
        DetailGutter.Width = new GridLength(UsesCodex ? 0 : 10);
        ZaiPane.Visibility = UsesComparison ? Visibility.Visible : Visibility.Collapsed;
        ZaiColumn.Width = UsesComparison ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ZaiGutter.Width = new GridLength(UsesComparison ? 10 : 0);
    }

    private async void ToggleCapture_Click(object sender, RoutedEventArgs e) => await ToggleCaptureAsync();

    private async Task ToggleCaptureAsync()
    {
        if (!IsLoaded || !CaptureButton.IsEnabled || choosingShortcut)
            return;

        if (stop is not null)
            StopCapture();
        else
            await StartCaptureAsync();
    }

    private async Task StartCaptureAsync()
    {
        if (stop is not null || sending || Devices.SelectedItem is not OutputDevice device)
            return;

        if (Transcript.Text.Length > 0 && (UsesCodex || pendingGemini is not null || pendingDetail is not null ||
                (UsesComparison && pendingZai is not null) || pendingDelivery is not null) && MessageBox.Show(this,
                "Phiên mới sẽ thay thế bản phiên âm trên màn hình. Bắt đầu phiên mới?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        using var stopSource = new CancellationTokenSource();
        using var abortSource = new CancellationTokenSource();
        stop = stopSource;
        abort = abortSource;
        sendOnStop = false;
        stopRequestedAt = null;
        TranscriptionTiming.Text = "";
        pendingDelivery = null;
        pendingGemini = null;
        pendingZai = null;
        ResetDetailReply();
        awaitingReply = null;
        RetryButton.Visibility = Visibility.Collapsed;
        ZaiRetryButton.Visibility = Visibility.Collapsed;
        UpdateReplyHeading();
        ReplyText.Clear();
        ZaiReplyText.Clear();
        GeminiTiming.Text = "";
        ZaiTiming.Text = "";
        ReplyPlaceholder.Visibility = Visibility.Collapsed;
        ZaiPlaceholder.Visibility = Visibility.Collapsed;
        SetRunning(true);
        Transcript.Clear();
        ClearPreview();
        Placeholder.Text = "Văn bản sẽ xuất hiện ở đây khi nhận diện được lời nói.";
        Placeholder.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = false;
        Dropped.Text = "";
        Status.Text = "Đang chuẩn bị…";

        session = RunSessionAsync(device.Id, (string)SpeechLanguage.SelectedValue, stopSource.Token, abortSource.Token);
        await session;
        session = null;
        stop = null;
        abort = null;
        SetRunning(false);
    }

    private async Task RunSessionAsync(string deviceId, string language, CancellationToken stopToken, CancellationToken abortToken)
    {
        var progress = new Progress<string>(message =>
        {
            if (!stopToken.IsCancellationRequested)
                Status.Text = message;
        });
        var updates = new Progress<TranscriptUpdate>(ShowTranscriptUpdate);
        var drops = new Progress<int>(count =>
            Dropped.Text = $"Đã bỏ qua {count} đoạn vì máy xử lý không kịp.");

        try
        {
            await Task.Run(() => transcriber.RunAsync(deviceId, language, progress, updates, drops, stopToken, abortToken));
            // Let all final transcript updates reach the UI before capturing the text to send.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (sendOnStop && !string.IsNullOrWhiteSpace(Transcript.Text))
            {
                if (stopRequestedAt is long stoppedAt)
                    TranscriptionTiming.Text = $"Chốt phiên âm sau {Stopwatch.GetElapsedTime(stoppedAt).TotalSeconds:0.00} giây kể từ lúc bấm Dừng.";

                if (UsesCodex)
                    pendingDelivery = new CodexTranscriptDelivery(Transcript.Text);
                else
                {
                    pendingGemini = Transcript.Text;
                    pendingDetail = Transcript.Text;
                    if (UsesComparison)
                        pendingZai = Transcript.Text;
                }
                await SendPendingAsync(stoppedAt: stopRequestedAt);
            }
            else
            {
                Status.Text = Transcript.Text.Length > 0 ? "Đã dừng." : "Đã dừng. Chưa nhận diện được lời nói.";
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested || abortToken.IsCancellationRequested)
        {
            Status.Text = "Đã dừng.";
        }
        catch (Exception error)
        {
            Status.Text = $"Phiên âm bị gián đoạn. {error.Message}";
        }
        finally
        {
            ClearPreview();
            Placeholder.Visibility = Transcript.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowTranscriptUpdate(TranscriptUpdate update)
    {
        if (update.IsPreview)
        {
            if (update.Lines.Count > 0)
            {
                LivePreview.Text = string.Join(" ", update.Lines.Select(line => line.Text));
                PreviewPanel.Visibility = Visibility.Visible;
                Placeholder.Visibility = Visibility.Collapsed;
            }
            return;
        }

        ClearPreview();
        if (update.Lines.Count > 0)
        {
            Transcript.AppendText(string.Join(Environment.NewLine, update.Lines.Select(line => line.Format())) + Environment.NewLine);
            Transcript.ScrollToEnd();
            SaveButton.IsEnabled = true;
        }
        Placeholder.Visibility = Transcript.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPreview()
    {
        LivePreview.Text = "";
        PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void SetRunning(bool running)
    {
        Devices.IsEnabled = !running;
        RefreshDevices.IsEnabled = !running;
        SpeechLanguage.IsEnabled = !running;
        ReplyProvider.IsEnabled = !running;
        ApiKeysButton.IsEnabled = !running;
        NewConversationButton.IsEnabled = !running;
        UpdateCaptureButton();
    }

    private void StopCapture()
    {
        if (stop is null || sendOnStop)
            return;

        sendOnStop = true;
        stopRequestedAt = Stopwatch.GetTimestamp();
        UpdateCaptureButton();
        ClearPreview();
        Status.Text = "Đang dừng và xử lý phần âm thanh còn lại…";
        stop.Cancel();
    }

    private void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (stop is not null || sending || UsesCodex)
            return;

        geminiInteractionId = null;
        detailInteractionId = null;
        ResetDetailReply();
        zaiHistory.Clear();
        pendingZai = null;
        ZaiRetryButton.Visibility = Visibility.Collapsed;
        ZaiReplyText.Clear();
        ZaiTiming.Text = "";
        ZaiPlaceholder.Visibility = Visibility.Collapsed;
        if (pendingDelivery is null && awaitingReply is null)
        {
            pendingGemini = null;
            RetryButton.Visibility = Visibility.Collapsed;
            UpdateReplyHeading();
            ReplyText.Clear();
            GeminiTiming.Text = "";
            ReplyPlaceholder.Visibility = Visibility.Collapsed;
        }
        Status.Text = "Đã bắt đầu cuộc phỏng vấn mới.";
    }

    private void ResetDetailReply()
    {
        pendingDetail = null;
        DetailRetryButton.Visibility = Visibility.Collapsed;
        DetailReplyText.Clear();
        DetailTiming.Text = "";
        DetailPlaceholder.Visibility = Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sending)
            return;

        var target = ReferenceEquals(sender, ZaiRetryButton) ? ReplyTarget.Zai
            : ReferenceEquals(sender, DetailRetryButton) ? ReplyTarget.Detail : ReplyTarget.Primary;
        session = SendPendingAsync(target);
        await session;
        session = null;
    }

    private async Task SendPendingAsync(ReplyTarget targets = ReplyTarget.All, long? stoppedAt = null)
    {
        var sendGemini = targets.HasFlag(ReplyTarget.Primary) && pendingGemini is not null;
        var sendDetail = targets.HasFlag(ReplyTarget.Detail) && pendingDetail is not null;
        var sendZaiReply = targets.HasFlag(ReplyTarget.Zai) && pendingZai is not null;
        var sendCodex = targets.HasFlag(ReplyTarget.Primary) && (pendingDelivery is not null || awaitingReply is not null);
        if (sending || (!sendGemini && !sendDetail && !sendZaiReply && !sendCodex))
            return;

        sending = true;
        UpdateCaptureButton();
        ReplyProvider.IsEnabled = false;
        ApiKeysButton.IsEnabled = false;
        NewConversationButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        DetailRetryButton.IsEnabled = false;
        ZaiRetryButton.IsEnabled = false;
        try
        {
            if (sendGemini || sendDetail || sendZaiReply)
            {
                if (closing)
                    return;

                Status.Text = "";
                using var apiCancellation = new CancellationTokenSource();
                replyWait = apiCancellation;
                await Task.WhenAll(
                    sendGemini ? SendGeminiAsync(pendingGemini!, stoppedAt, apiCancellation.Token) : Task.CompletedTask,
                    sendDetail ? SendDetailAsync(pendingDetail!, stoppedAt, apiCancellation.Token) : Task.CompletedTask,
                    sendZaiReply ? SendZaiAsync(pendingZai!, stoppedAt, apiCancellation.Token) : Task.CompletedTask);
                Status.Text = pendingGemini is null && pendingDetail is null && pendingZai is null
                    ? ""
                    : "Có phản hồi chưa hoàn tất.";
                return;
            }

            RetryButton.Visibility = Visibility.Collapsed;
            if (pendingDelivery is { } delivery)
            {
                Status.Text = $"Đang gửi bản phiên âm vào “{CodexTranscriptDelivery.ThreadTitle}”…";
                ReplyPlaceholder.Text = "Đang gửi bản phiên âm…";
                ReplyPlaceholder.Visibility = Visibility.Visible;
                await delivery.SendAsync();
                awaitingReply = delivery.Receipt;
                pendingDelivery = null;
            }

            if (closing)
                return;

            Status.Text = "Đã gửi bản phiên âm. Đang chờ Codex trả lời…";
            ReplyPlaceholder.Text = "Đang chờ phản hồi từ Codex…";
            ReplyPlaceholder.Visibility = Visibility.Visible;
            using var cancellation = new CancellationTokenSource();
            replyWait = cancellation;
            ReplyText.Text = await CodexReplyReader.WaitAsync(awaitingReply!, TryOpenChat, cancellation.Token);
            ReplyPlaceholder.Visibility = Visibility.Collapsed;
            awaitingReply = null;
            Status.Text = "Đã nhận phản hồi từ Codex.";
        }
        catch (OperationCanceledException) when (closing)
        {
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or TimeoutException)
        {
            var checkingCodexReply = pendingDelivery is null;
            Status.Text = checkingCodexReply ? "Đã gửi bản phiên âm." : error.Message;
            if (ReplyText.Text.Length == 0)
                ReplyPlaceholder.Text = error.Message;
            RetryButton.Content = checkingCodexReply ? "Kiểm tra phản hồi" : "Gửi lại";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            replyWait = null;
            sending = false;
            RetryButton.IsEnabled = true;
            DetailRetryButton.IsEnabled = true;
            ZaiRetryButton.IsEnabled = true;
            ReplyProvider.IsEnabled = stop is null;
            ApiKeysButton.IsEnabled = stop is null;
            NewConversationButton.IsEnabled = stop is null;
            UpdateCaptureButton();
        }
    }

    private Task SendGeminiAsync(string transcript, long? stoppedAt, CancellationToken token) => ShowApiReplyAsync(
        ReplyText, ReplyPlaceholder, GeminiTiming, RetryButton, "Gửi lại Gemini", ApiProvider.Gemini, stoppedAt, async showText =>
        {
            var reply = await GeminiReplyClient.StreamAsync(transcript, geminiInteractionId, showText, token);
            geminiInteractionId = reply.Value;
            pendingGemini = null;
            return reply.Tokens;
        });

    private Task SendDetailAsync(string transcript, long? stoppedAt, CancellationToken token) => ShowApiReplyAsync(
        DetailReplyText, DetailPlaceholder, DetailTiming, DetailRetryButton, "Gửi lại bản chi tiết", ApiProvider.Gemini, stoppedAt, async showText =>
        {
            var reply = await GeminiReplyClient.StreamAsync(transcript, detailInteractionId, showText, token, detailed: true);
            detailInteractionId = reply.Value;
            pendingDetail = null;
            return reply.Tokens;
        });

    private Task SendZaiAsync(string transcript, long? stoppedAt, CancellationToken token) => ShowApiReplyAsync(
        ZaiReplyText, ZaiPlaceholder, ZaiTiming, ZaiRetryButton, "Gửi lại Z.AI", ApiProvider.Zai, stoppedAt, async showText =>
        {
            var question = new ZaiMessage("user", zaiHistory.Count == 0 ? InterviewContext.FirstTurn(transcript) : transcript);
            var answer = await ZaiReplyClient.StreamAsync(question, zaiHistory, showText, token);
            zaiHistory.Add(question);
            zaiHistory.Add(new ZaiMessage("assistant", answer.Value));
            pendingZai = null;
            return answer.Tokens;
        });

    private static async Task ShowApiReplyAsync(TextBox output, TextBlock placeholder, TextBlock timing,
        Button retry, string retryLabel, ApiProvider provider, long? stoppedAt, Func<Action<string>, Task<long?>> send)
    {
        output.Clear();
        placeholder.Text = "Đang chờ phản hồi…";
        placeholder.Visibility = Visibility.Visible;
        timing.Text = "";
        retry.Visibility = Visibility.Collapsed;
        var clock = Stopwatch.StartNew();
        string? firstTextLabel = null;

        try
        {
            var tokens = await send(text =>
            {
                if (firstTextLabel is null && !string.IsNullOrWhiteSpace(text))
                {
                    placeholder.Visibility = Visibility.Collapsed;
                    firstTextLabel = $"API ra chữ đầu sau {clock.Elapsed.TotalSeconds:0.00} giây";
                    if (stoppedAt is long start)
                        firstTextLabel += $" · Sau Dừng {Stopwatch.GetElapsedTime(start).TotalSeconds:0.00} giây";
                    timing.Text = firstTextLabel;
                }
                output.AppendText(text);
            });
            var elapsed = clock.Elapsed;
            var saved = ApiUsageStore.Record(provider, tokens) is not null;
            var usage = tokens is long count ? $" · {count:N0} token" : "";
            timing.Text = $"{firstTextLabel} · API xong sau {elapsed.TotalSeconds:0.00} giây{usage}" +
                (saved ? "" : " · Chưa lưu được thống kê");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        {
            timing.Text = error.Message;
            if (firstTextLabel is null)
                placeholder.Text = "Chưa nhận được câu trả lời.";
            retry.Content = retryLabel;
            retry.Visibility = Visibility.Visible;
        }
    }

    private void OpenChat_Click(object sender, RoutedEventArgs e) => TryOpenChat();

    private void TryOpenChat()
    {
        try
        {
            CodexTranscriptDelivery.OpenChat();
        }
        catch (Win32Exception)
        {
            ReplyPlaceholder.Text = $"Hãy mở chat “{CodexTranscriptDelivery.ThreadTitle}” trong Codex.";
        }
        catch (IOException error)
        {
            Status.Text = error.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Văn bản UTF-8 (*.txt)|*.txt",
            DefaultExt = ".txt",
            FileName = $"Phien-am-{DateTime.Now:yyyy-MM-dd-HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, Transcript.Text, Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Không lưu được bản phiên âm", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (session is null || session.IsCompleted)
            return;

        e.Cancel = true;
        if (closing)
            return;

        closing = true;
        IsEnabled = false;
        replyWait?.Cancel();
        if (!sendOnStop && !sending)
        {
            stop?.Cancel();
            abort?.Cancel();
        }
        await session;
        Close();
    }
}
