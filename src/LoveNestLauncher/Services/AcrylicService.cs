using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LoveNestLauncher.Services;

public sealed class AcrylicService
{
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentDisabled = 0;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmsbtNone = 1;

    public bool Enable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (TryCompositionAcrylic(hwnd))
        {
            return true;
        }

        return TryNativeBackdrop(hwnd);
    }

    public void Disable(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var backdrop = DwmsbtNone;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch
        {
            // Older Windows versions do not support this attribute.
        }

        TrySetAccent(hwnd, AccentDisabled, 0);
    }

    private static bool TryNativeBackdrop(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22621)
        {
            return false;
        }

        try
        {
            var backdrop = DwmsbtTransientWindow;
            var result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            if (result != 0)
            {
                return false;
            }

            var corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCompositionAcrylic(IntPtr hwnd)
    {
        // A light rose tint in 0xAABBGGRR form.
        var tint = unchecked((int)0x50F7EEF6);
        return TrySetAccent(hwnd, AccentEnableAcrylicBlurBehind, tint);
    }

    private static bool TrySetAccent(IntPtr hwnd, int state, int gradientColor)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = state == AccentDisabled ? 0 : 2,
                GradientColor = gradientColor,
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
