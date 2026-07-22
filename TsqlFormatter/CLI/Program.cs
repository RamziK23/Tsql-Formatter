using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// ── Mode: --auto  (recommended for SSMS) ──────────────────────────────────────
// Formats the current editor SELECTION in place: copies it, formats it, pastes it
// back — one hotkey, no manual Ctrl+C / Ctrl+V, no messages, no window.
//
// SSMS External Tools setup (Tools ▸ External Tools ▸ Add):
//   Title:     Format SQL
//   Command:   C:\Tools\TsqlFormatter\TsqlFormatter.exe
//   Arguments: --auto
//   [ ] Use Output window      ← leave UNCHECKED (no messages / no pane)
//   [ ] Prompt for arguments   ← unchecked
// Then Tools ▸ Options ▸ Environment ▸ Keyboard, bind "Tools.ExternalCommand<N>"
// (N = position in the External Tools list) to a hotkey.
//
// Usage: select SQL → press the hotkey → formatted SQL replaces the selection.
if (args.Length == 1 && (args[0] == "--auto" || args[0] == "--format"))
    return AutoFormat.Run();

// ── Mode: --clipboard ─────────────────────────────────────────────────────────
// Formats whatever text is on the clipboard, in place, silently (copy → run → paste).
if (args.Length == 1 && args[0] == "--clipboard")
{
    try
    {
        var source = WinClipboard.GetText();
        if (!string.IsNullOrWhiteSpace(source))
            WinClipboard.SetText(FormatterEngine.FormatSource(source));
        return 0;
    }
    catch { return 3; }
}

// ── Mode: --stdin ─────────────────────────────────────────────────────────────
if (args.Length == 1 && args[0] == "--stdin")
{
    try
    {
        var source = Console.In.ReadToEnd();
        Console.Out.Write(FormatterEngine.FormatSource(source));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return 3; }
}

// ── Mode: <filepath> ─────────────────────────────────────────────────────────
var filePath = args.Length > 0 ? args[0] : null;
if (filePath == null)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  TsqlFormatter.exe --auto        — format the current editor selection in place (SSMS)");
    Console.Error.WriteLine("  TsqlFormatter.exe --clipboard   — format the clipboard in place");
    Console.Error.WriteLine("  TsqlFormatter.exe --stdin       — read from stdin, write to stdout");
    Console.Error.WriteLine("  TsqlFormatter.exe <file.sql>    — format file in-place");
    return 1;
}
if (!File.Exists(filePath))
{
    Console.Error.WriteLine("File not found: " + filePath);
    return 2;
}
try
{
    var (source, encoding) = ReadWithEncoding(filePath);
    var result = FormatterEngine.FormatSource(source);
    File.WriteAllText(filePath, result, encoding);
    Console.WriteLine("Formatted: " + filePath);
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("Error: " + ex.Message); return 3; }

// ─── helpers ─────────────────────────────────────────────────────────────────

static (string text, Encoding enc) ReadWithEncoding(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
    try   { var s = new UTF8Encoding(false, true); return (s.GetString(bytes), new UTF8Encoding(false)); }
    catch { var s = Encoding.GetEncoding(1251);    return (s.GetString(bytes), s); }
}

// ─── Windows clipboard via P/Invoke (no PowerShell, no Windows Forms) ────────
static class WinClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] static extern bool   OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool   CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool   EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool   GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr h);

    public static string GetText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return string.Empty;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return string.Empty;
            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) return string.Empty;
            try   { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void SetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var h     = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (h == IntPtr.Zero) throw new InvalidOperationException("GlobalAlloc failed");
        var ptr   = GlobalLock(h);
        if (ptr == IntPtr.Zero) { GlobalFree(h); throw new InvalidOperationException("GlobalLock failed"); }
        try   { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
        finally { GlobalUnlock(h); }

        if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(h); throw new InvalidOperationException("OpenClipboard failed"); }
        try   { EmptyClipboard(); SetClipboardData(CF_UNICODETEXT, h); }
        finally { CloseClipboard(); }
    }
}

// ─── Selection auto-format: synthesize Ctrl+C / Ctrl+V around the formatter ───
// The tool has no access to the editor's selection, so it drives the clipboard with
// synthetic keystrokes: copy the selection, format it, paste it back. Fully silent —
// on any problem (nothing selected, parse error) it does nothing and leaves the text.
static class AutoFormat
{
    private const byte VK_CONTROL = 0x11, VK_C = 0x43, VK_V = 0x56;
    private const int  VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")] private static extern void  keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint  GetClipboardSequenceNumber();

    public static int Run()
    {
        try
        {
            // The tool is launched by a Ctrl-based hotkey; wait for the user to release
            // the modifier keys so our synthetic Ctrl+C / Ctrl+V aren't combined with them.
            WaitModifiersReleased(700);

            uint seq = GetClipboardSequenceNumber();
            SendCtrl(VK_C);                                   // copy the current selection
            var source = WaitClipboardChanged(seq, 1500);
            if (string.IsNullOrWhiteSpace(source)) return 0;  // nothing selected — do nothing

            string result;
            try   { result = FormatterEngine.FormatSource(source); }
            catch { return 0; }                               // never overwrite with garbage

            WinClipboard.SetText(result);
            Thread.Sleep(40);
            SendCtrl(VK_V);                                   // paste over the selection
            Thread.Sleep(40);
            return 0;
        }
        catch { return 0; }
    }

    /// <summary>Waits until the copy raises the clipboard sequence number, then returns its text.</summary>
    private static string WaitClipboardChanged(uint prevSeq, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (GetClipboardSequenceNumber() != prevSeq)
            {
                var text = WinClipboard.GetText();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            Thread.Sleep(20);
        }
        return string.Empty;
    }

    /// <summary>Blocks until Ctrl/Shift/Alt/Win are all up (or the timeout elapses).</summary>
    private static void WaitModifiersReleased(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool down = IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_MENU)
                     || IsDown(VK_LWIN)    || IsDown(VK_RWIN);
            if (!down) { Thread.Sleep(30); return; }          // small settle after release
            Thread.Sleep(15);
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendCtrl(byte vk)
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
