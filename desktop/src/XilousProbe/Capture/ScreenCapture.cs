using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using XilousProbe.Uia;

namespace XilousProbe.Capture;

/// <summary>
/// Captures a screen rectangle as PNG bytes. The overlay windows are marked
/// WDA_EXCLUDEFROMCAPTURE, so the outline drawn over the element is not in it.
/// </summary>
public static partial class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hWnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hWnd, nint hDC);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint ho);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    public static byte[] CapturePng(PxRect r)
    {
        if (r.IsEmpty) throw new ArgumentException("Nothing to capture: the element has no size.");
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        nint old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("The screen could not be read.");
            SelectObject(mem, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }
}
