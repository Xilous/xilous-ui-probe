using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace XilousProbe.Capture;

/// <summary>
/// Writes the Windows clipboard from any thread. The clipboard is an STA resource,
/// and another app holding it open makes a write fail for a moment, so writes go
/// through the UI thread with a few retries.
/// </summary>
public static class ClipboardService
{
    public static Task SetTextAsync(string text) => OnUiAsync(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));

    /// <summary>Puts a PNG on the clipboard both as "PNG" (keeps transparency, read by browsers and chat apps) and as a bitmap.</summary>
    public static Task SetPngAsync(byte[] png) => OnUiAsync(() =>
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png), false);
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        data.SetImage(decoder.Frames[0]);
        Clipboard.SetDataObject(data, true);
    });

    private static Task OnUiAsync(Action write)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(async () =>
        {
            for (int attempt = 0; ; attempt++)
            {
                try { write(); return; }
                catch (COMException) when (attempt < 5) { await Task.Delay(60); }
                catch (ExternalException) when (attempt < 5) { await Task.Delay(60); }
            }
        }).Task.Unwrap();
    }
}
