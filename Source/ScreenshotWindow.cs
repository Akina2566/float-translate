using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FloatTranslate
{
    public sealed class ScreenshotWindow : Window
    {
        private readonly Drawing.Bitmap screenshot;
        private readonly Drawing.Rectangle desktop;
        private readonly Canvas surface;
        private readonly Rectangle[] shades = new Rectangle[4];
        private readonly Rectangle selection;
        private readonly TextBlock dimensions;
        private Point start;
        private bool dragging;
        public byte[] ImageBytes { get; private set; }
        public ScreenshotWindow()
        {
            desktop = Forms.SystemInformation.VirtualScreen;
            screenshot = new Drawing.Bitmap(desktop.Width, desktop.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
            try { using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(screenshot)) graphics.CopyFromScreen(desktop.Left, desktop.Top, 0, 0, desktop.Size, Drawing.CopyPixelOperation.SourceCopy); }
            catch { screenshot.Dispose(); throw new Exception("无法截图，请检查远程桌面或屏幕捕获权限。"); }
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; Cursor = Cursors.Cross;
            Left = desktop.Left; Top = desktop.Top; Width = desktop.Width; Height = desktop.Height;
            Background = Brushes.Black;
            var grid = new Grid(); Content = grid;
            var image = new Image { Source = ToSource(screenshot), Stretch = Stretch.Fill }; grid.Children.Add(image);
            surface = new Canvas { Background = Brushes.Transparent }; grid.Children.Add(surface);
            for (int i = 0; i < 4; i++) { shades[i] = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(130, 10, 20, 38)), IsHitTestVisible = false }; surface.Children.Add(shades[i]); }
            selection = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(70, 160, 255)), StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed }; surface.Children.Add(selection);
            dimensions = new TextBlock { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(230, 22, 34, 52)), Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false, Visibility = Visibility.Collapsed }; surface.Children.Add(dimensions);
            var hint = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 22, 34, 52)), CornerRadius = new CornerRadius(13), Padding = new Thickness(22, 12, 22, 12), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 48), IsHitTestVisible = false };
            hint.Child = new TextBlock { Text = "拖动框选需要翻译的文字    ·    Esc 取消", Foreground = Brushes.White, FontSize = 15 }; grid.Children.Add(hint);
            SourceInitialized += delegate { Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), desktop.Left, desktop.Top, desktop.Width, desktop.Height, 0x0040); };
            Loaded += delegate { Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), desktop.Left, desktop.Top, desktop.Width, desktop.Height, 0x0040); Focus(); RenderSelection(new Rect(0, 0, 0, 0)); };
            SizeChanged += delegate { if (!dragging) RenderSelection(new Rect(0, 0, 0, 0)); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
            surface.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { start = e.GetPosition(surface); dragging = true; surface.CaptureMouse(); selection.Visibility = Visibility.Visible; dimensions.Visibility = Visibility.Visible; RenderSelection(new Rect(start, start)); };
            surface.MouseMove += delegate(object sender, MouseEventArgs e) { if (dragging) RenderSelection(Normalized(e.GetPosition(surface))); };
            surface.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!dragging) return;
                dragging = false; surface.ReleaseMouseCapture();
                Rect area = Normalized(e.GetPosition(surface));
                Drawing.Rectangle pixels = MapToPixels(area, surface.ActualWidth, surface.ActualHeight, desktop.Width, desktop.Height);
                if (pixels.Width < 8 || pixels.Height < 8) { DialogResult = false; return; }
                using (Drawing.Bitmap crop = screenshot.Clone(pixels, Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var stream = new MemoryStream()) { crop.Save(stream, Drawing.Imaging.ImageFormat.Png); ImageBytes = stream.ToArray(); }
                DialogResult = true;
            };
            Closed += delegate { screenshot.Dispose(); };
        }
        private Rect Normalized(Point end)
        {
            end.X = Math.Max(0, Math.Min(surface.ActualWidth, end.X)); end.Y = Math.Max(0, Math.Min(surface.ActualHeight, end.Y));
            return new Rect(start, end);
        }
        private void Shade(int index, double x, double y, double w, double h)
        {
            Canvas.SetLeft(shades[index], x); Canvas.SetTop(shades[index], y); shades[index].Width = Math.Max(0, w); shades[index].Height = Math.Max(0, h);
        }
        private void RenderSelection(Rect area)
        {
            double w = surface.ActualWidth, h = surface.ActualHeight;
            Shade(0, 0, 0, w, area.Top); Shade(1, 0, area.Top, area.Left, area.Height);
            Shade(2, area.Right, area.Top, w - area.Right, area.Height); Shade(3, 0, area.Bottom, w, h - area.Bottom);
            Canvas.SetLeft(selection, area.Left); Canvas.SetTop(selection, area.Top); selection.Width = area.Width; selection.Height = area.Height;
            Drawing.Rectangle pixels = MapToPixels(area, w, h, desktop.Width, desktop.Height);
            dimensions.Text = pixels.Width + " × " + pixels.Height;
            Canvas.SetLeft(dimensions, Math.Max(0, Math.Min(w - 130, area.Left))); Canvas.SetTop(dimensions, area.Top > 32 ? area.Top - 31 : area.Bottom + 7);
        }
        public static Drawing.Rectangle MapToPixels(Rect area, double width, double height, int pixelWidth, int pixelHeight)
        {
            if (width <= 0 || height <= 0) return Drawing.Rectangle.Empty;
            int x = Math.Max(0, Math.Min(pixelWidth, (int)Math.Floor(area.Left * pixelWidth / width)));
            int y = Math.Max(0, Math.Min(pixelHeight, (int)Math.Floor(area.Top * pixelHeight / height)));
            int right = Math.Max(x, Math.Min(pixelWidth, (int)Math.Ceiling(area.Right * pixelWidth / width)));
            int bottom = Math.Max(y, Math.Min(pixelHeight, (int)Math.Ceiling(area.Bottom * pixelHeight / height)));
            return new Drawing.Rectangle(x, y, right - x, bottom - y);
        }
        private static BitmapSource ToSource(Drawing.Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
        }
    }
}
