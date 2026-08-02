using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VolvoSplitter.Native;

/// <summary>
/// Setzt die Titelleiste auf das dunkle Windows-Erscheinungsbild, damit sie
/// nicht als heller Streifen über der Anwendung steht. Fensterverwaltung,
/// Andockvorschau und abgerundete Ecken bleiben die von Windows.
/// </summary>
public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);

    public static void ApplyDark(Window window)
    {
        void Apply()
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int on = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));

            // COLORREF: 0x00BBGGRR — passend zum Hintergrund der Anwendung.
            int caption = 0x0016110E;
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

            int border = 0x00382D24;
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }

        if (window.IsLoaded) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }
}
