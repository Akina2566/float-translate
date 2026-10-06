using System;
using System.Windows;
using System.Windows.Media;

namespace FloatTranslate
{
    public static class Appearance
    {
        public static void ApplyPalette(Settings settings, bool acrylic)
        {
            double transparency = Math.Max(15, Math.Min(70, settings.GlassTransparency));
            byte tint = acrylic ? (byte)Math.Round(255 * (1 - transparency / 100)) : (byte)255;
            Set("ShellBrush", tint, 248, 250, 252);
            Set("SidebarBrush", acrylic ? (byte)128 : (byte)255, 233, 240, 250);
            Set("CardBrush", acrylic ? (byte)215 : (byte)255, 255, 255, 255);
            Set("ResultBrush", acrylic ? (byte)218 : (byte)255, 240, 246, 255);
            Set("ShortcutBrush", acrylic ? (byte)155 : (byte)255, 239, 243, 249);
        }
        private static void Set(string name, byte alpha, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b)); brush.Freeze(); Application.Current.Resources[name] = brush;
        }
    }
}
