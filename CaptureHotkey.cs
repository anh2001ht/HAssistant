using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Interop;

namespace SystemAudioTranscript;

internal readonly record struct CaptureShortcut(Key Key, ModifierKeys Modifiers, bool Shared = false)
{
    public static CaptureShortcut Default { get; } = new(Key.F8, ModifierKeys.Control | ModifierKeys.Shift);

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemAudioTranscript", "capture-shortcut.json");

    [JsonIgnore]
    public bool IsSupported => Key != Key.F12 &&
        (Key is >= Key.F1 and <= Key.F24 || Modifiers != ModifierKeys.None) &&
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0 &&
        Enum.IsDefined(Key) && Key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) &&
        KeyInterop.VirtualKeyFromKey(Key) != 0;

    public override string ToString() =>
        $"{(Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")}" +
        $"{(Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")}" +
        $"{(Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")}{Key}";

    public static CaptureShortcut Load()
    {
        try
        {
            var shortcut = JsonSerializer.Deserialize<CaptureShortcut>(File.ReadAllText(SettingsPath));
            return shortcut.IsSupported ? shortcut : Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Default;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = $"{SettingsPath}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed class CaptureHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int LowLevelKeyboard = 13;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SystemKeyDown = 0x0104;
    private const int SystemKeyUp = 0x0105;
    private const uint NoRepeat = 0x4000;
    private const int HotkeyAlreadyRegistered = 1409;
    private readonly HwndSource source;
    private readonly KeyboardHook sharedCallback;
    private nint sharedHook;
    private int registeredId;
    private int sharedVirtualKey;
    private bool sharedKeyDown;
    private CaptureShortcut registeredShortcut;

    public CaptureHotkey(HwndSource source)
    {
        this.source = source;
        source.AddHook(OnMessage);
        sharedCallback = OnSharedKeyboard;
    }

    public event Action? Pressed;
    public bool IsShared => sharedHook != 0;

    public bool TryRegister(CaptureShortcut shortcut)
    {
        if ((registeredId != 0 || IsShared) && registeredShortcut == shortcut)
            return true;

        var nextId = registeredId == 1 ? 2 : 1;
        var virtualKey = KeyInterop.VirtualKeyFromKey(shortcut.Key);
        var registered = false;
        if (!shortcut.Shared)
        {
            registered = RegisterHotKey(source.Handle, nextId, NoRepeat | (uint)shortcut.Modifiers, (uint)virtualKey);
            if (!registered && Marshal.GetLastWin32Error() != HotkeyAlreadyRegistered)
                return false;
        }
        if (!registered && sharedHook == 0)
        {
            sharedHook = SetWindowsHookEx(LowLevelKeyboard, sharedCallback, GetModuleHandle(null), 0);
            if (sharedHook == 0)
                return false;
        }

        if (registeredId != 0)
            UnregisterHotKey(source.Handle, registeredId);
        if (registered)
            RemoveSharedHook();
        registeredId = registered ? nextId : 0;
        registeredShortcut = shortcut;
        if (!registered)
        {
            // Observe the chosen shortcut without taking ownership of it in Windows.
            sharedVirtualKey = virtualKey;
            sharedKeyDown = IsKeyDown(virtualKey);
        }
        return true;
    }

    public void Dispose()
    {
        RemoveSharedHook();
        if (registeredId != 0 && !source.IsDisposed)
            UnregisterHotKey(source.Handle, registeredId);
        if (!source.IsDisposed)
            source.RemoveHook(OnMessage);
        registeredId = 0;
    }

    private void RemoveSharedHook()
    {
        if (sharedHook != 0)
            UnhookWindowsHookEx(sharedHook);
        sharedHook = 0;
    }

    private nint OnSharedKeyboard(int code, nint message, nint data)
    {
        if (code == 0 && sharedHook != 0 && Marshal.ReadInt32(data) == sharedVirtualKey)
        {
            if (message is KeyUp or SystemKeyUp)
                sharedKeyDown = false;
            else if ((message is KeyDown or SystemKeyDown) && !sharedKeyDown)
            {
                sharedKeyDown = true;
                if (ReadModifiers() == registeredShortcut.Modifiers)
                    QueuePressed();
            }
        }

        // Keep the registered owner's shortcut and normal keyboard behavior intact.
        return CallNextHookEx(0, code, message, data);
    }

    private static ModifierKeys ReadModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsKeyDown(0x10)) modifiers |= ModifierKeys.Shift;
        if (IsKeyDown(0x11)) modifiers |= ModifierKeys.Control;
        if (IsKeyDown(0x12)) modifiers |= ModifierKeys.Alt;
        if (IsKeyDown(0x5B) || IsKeyDown(0x5C)) modifiers |= ModifierKeys.Windows;

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => GetAsyncKeyState(virtualKey) < 0;

    private nint OnMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && registeredId != 0 && wParam == registeredId)
        {
            handled = true;
            QueuePressed();
        }
        return 0;
    }

    private void QueuePressed() => source.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!source.IsDisposed && IsWindowEnabled(source.Handle))
            Pressed?.Invoke();
    }));

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);

    private delegate nint KeyboardHook(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, KeyboardHook callback, nint module, uint threadId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint handle, int id);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint handle);
}
