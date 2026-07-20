using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TsqlFormatter.Core;
using TsqlFormatter.Formatting;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// ── Mode: --clipboard ─────────────────────────────────────────────────────────
// Reads SQL from the Windows clipboard, formats it, writes the result back.
// No PowerShell required — works in any corporate environment.
//
// SSMS External Tools setup:
//   Title:     Format Selection (clipboard)
//   Command:   C:\Tools\TsqlFormatter\TsqlFormatter.exe
//   Arguments: --clipboard
//   ✅ Use Output window
//
// Usage: select SQL → Ctrl+C → run tool → Ctrl+V
if (args.Length == 1 && args[0] == "--clipboard")
{
    try
    {
        var source = WinClipboard.GetText();
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("Clipboard is empty — copy your SQL selection first.");
            return 0;
        }

        var result = FormatterEngine.FormatSource(source);

        WinClipboard.SetText(result);
        Console.WriteLine("Done — paste with Ctrl+V");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Clipboard error: " + ex.Message);
        return 3;
    }
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
    Console.Error.WriteLine("  TsqlFormatter.exe <file.sql>   — format file in-place");
    Console.Error.WriteLine("  TsqlFormatter.exe --stdin      — read from stdin, write to stdout");
    Console.Error.WriteLine("  TsqlFormatter.exe --clipboard  — format clipboard (no PowerShell needed)");
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
