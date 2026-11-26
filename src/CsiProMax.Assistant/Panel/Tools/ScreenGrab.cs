using System.Runtime.InteropServices;
using System.IO;

namespace CsiProMax.Tools;

/// <summary>
/// Grabs the pixels of an open ETABS view window. PrintWindow with PW_RENDERFULLCONTENT renders the ETABS main window
/// (DirectX view canvases included) even when other windows cover it; the view's rectangle is then cut out.
/// </summary>
internal static class ScreenGrab
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    private struct Rect { public int Left, Top, Right, Bottom; }
    private const uint PwRenderFullContent = 2;

    /// <summary>All of ETABS's main window as a PNG at most <paramref name="maxWidth"/> wide (the chat's camera button, capture_view_image).</summary>
    public static byte[] Capture(IntPtr main, int maxWidth)
    {
        if (main == IntPtr.Zero) throw new InvalidOperationException($"{Csi.Name}'s main window was not found.");
        if (IsIconic(main)) return WhileShown(main, () => Capture(main, maxWidth));
        if (!GetWindowRect(main, out var w)) throw new InvalidOperationException($"Could not read {Csi.Name}'s window position.");
        int width = w.Right - w.Left, height = w.Bottom - w.Top;
        if (width < 50 || height < 50) throw new InvalidOperationException($"{Csi.Name}'s window is too small to capture.");
        return Grab(main, width, height, 0, 0, width, height, maxWidth, new System.Windows.Media.Imaging.PngBitmapEncoder());
    }

    /// <summary>All of ETABS's main window as a JPEG at most <paramref name="maxWidth"/> wide (bug reports).</summary>
    public static byte[] Window(IntPtr main, int maxWidth)
    {
        if (main == IntPtr.Zero) throw new InvalidOperationException($"{Csi.Name}'s main window was not found.");
        if (IsIconic(main)) return WhileShown(main, () => Window(main, maxWidth));
        if (!GetWindowRect(main, out var w)) throw new InvalidOperationException($"Could not read {Csi.Name}'s window position.");
        int width = w.Right - w.Left, height = w.Bottom - w.Top;
        if (width < 50 || height < 50) throw new InvalidOperationException($"{Csi.Name}'s window is too small to capture.");
        return Grab(main, width, height, 0, 0, width, height, maxWidth, new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 70 });
    }

    /// <summary>
    /// A minimized window has no picture to take: it is shown without taking the focus, taken, and minimized again
    /// (SAFE 20 was minimized on the tester's PC and capture_view_image failed, 0.4.53, 2026-11-26).
    /// </summary>
    private static byte[] WhileShown(IntPtr main, Func<byte[]> take)
    {
        ShowWindow(main, 4 /* SW_SHOWNOACTIVATE */);
        try
        {
            for (var i = 0; i < 20 && IsIconic(main); i++) Thread.Sleep(100);
            if (IsIconic(main)) throw new InvalidOperationException($"{Csi.Name} is minimized and did not come up to be pictured; restore its window and try again.");
            Thread.Sleep(400); // its views redraw
            return take();
        }
        finally { ShowWindow(main, 7 /* SW_SHOWMINNOACTIVE */); }
    }

    private static byte[] Grab(IntPtr main, int width, int height, int x, int y, int cw, int ch, int maxWidth, System.Windows.Media.Imaging.BitmapEncoder encoder)
    {
        var windowDc = GetWindowDC(main);
        var memDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(windowDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(main, memDc, PwRenderFullContent))
                throw new InvalidOperationException($"Windows would not render {Csi.Name}'s window.");
            SelectObject(memDc, old);
            System.Windows.Media.Imaging.BitmapSource image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, new System.Windows.Int32Rect(x, y, cw, ch),
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            if (cw > maxWidth)
            {
                var k = maxWidth / (double)cw;
                image = new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(k, k));
            }
            if (encoder is System.Windows.Media.Imaging.JpegBitmapEncoder)
                image = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr24, null, 0);
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(main, windowDc);
        }
    }
}

/// <summary>
/// capture_view_image: a picture of the ETABS window as it shows the model now (its plan, elevation and 3D views, with
/// whatever is displayed: loads, deformed shape, diagrams, design ratios), so Claude can look at the model the way the
/// engineer sees it. The chat's camera button attaches the same picture.
/// </summary>
public sealed class CaptureViewImageTool : IEtabsTool
{
    public string Name => "capture_view_image";
    public string Description =>
        $"Takes a picture of the {Csi.Name} window as it shows the model now (its open views with whatever is displayed: the model, loads, " +
        "deformed shape, force diagrams, design ratios) and returns it as an image. Use it to see what the engineer sees, and to check " +
        $"the result of a change by eye. It shows what {Csi.Name} is showing; it does not change the views.";
    public string PropertiesJson => """{"pixel_width": {"type": "integer", "description": "The picture's width in pixels (400 to 2400, default 1600)."}}""";
    public string[] Required => [];
    public bool ModifiesModel => false;

    public ToolResult Execute(EtabsApp app, ToolArgs args)
    {
        var width = (int)Math.Min(2400, Math.Max(400, args.OptLong("pixel_width") ?? 1600));
        try
        {
            var png = ScreenGrab.Capture(CsiProMax.App.MainWindow, width);
            var title = app.ActiveUIDocument?.Title ?? "the model";
            return new ToolResult($"Picture of the {Csi.Name} window, view '{title}'.", PngImage: png);
        }
        catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
    }
}
