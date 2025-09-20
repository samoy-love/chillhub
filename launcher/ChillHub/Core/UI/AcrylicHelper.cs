// <copyright file="AcrylicHelper.cs" company="PlaceholderCompany">
// Copyright (c) 2025 ChillHub
// Licensed under the MIT License.
// </copyright>

namespace ChillHub.Core.UI
{
    using System;
    using System.Runtime.InteropServices;
    using System.Windows;
    using System.Windows.Interop;

    using Microsoft.Win32;

    public static class AcrylicHelper
    {
        // Apply title bar theme (dark/light) without enabling any blur/acrylic.
        public static void ApplyTitleBarTheme(Window window, bool isDark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                int useDark = isDark ? 1 : 0;

                // Try modern attribute id (Win11/Win10 1903+)
                int hr = DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                if (hr != 0)
                {
                    // Try legacy id (some Win10 builds)
                    const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
                    _ = DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
                }
            }
            catch
            {
            }
        }

        public static bool IsSystemAppsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var val = key.GetValue("AppsUseLightTheme");
                    if (val is int i)
                    {
                        return i == 0; // 0 = dark, 1 = light
                    }
                }
            }
            catch
            {
            }
            return false; // default to dark=false (light) if unknown
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attribute, ref int pvAttribute, int cbAttribute);

        private enum DWMWINDOWATTRIBUTE
        {
            DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        }
    }
}
