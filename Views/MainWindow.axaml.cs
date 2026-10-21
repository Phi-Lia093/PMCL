using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace PMCL.Views;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ApplyDarkTitleBar();
    }

    /// <summary>
    /// Paints the native Windows title bar dark so it blends with the launcher theme.
    /// Silently ignored on non-Windows platforms or older Windows builds.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

        // COLORREF is 0x00BBGGRR.
        var captionColor = 0x001B120E; // #0E121B
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));

        var textColor = 0x00F2F5FF; // #F2F5FF
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}