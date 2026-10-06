using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Forms = System.Windows.Forms;

namespace FloatTranslate
{
    public sealed class PopupWindow : Window
    {
        private readonly Settings settings;
        private readonly string original;
        private byte[] image;
        private readonly ApiClient api;
        private CancellationTokenSource cancellation;
        private readonly Border frame, originalPanel;
        private readonly TextBox output;
        private readonly TextBlock status;
        private readonly ProgressBar progress;
        private readonly Button copy, retry, pin, close, showOriginal, autoSize;
        private bool closed, originalExpanded, userSized;
        private HwndSource nativeSource;
        public event Action<HistoryEntry> Completed;

        public PopupWindow(Settings config, string text, byte[] screenshot, ApiClient client, Action showSettings)
        {
            settings = config.Copy(); original = text; image = screenshot; api = client;
            Title = "浮译"; Width = screenshot == null ? 400 : 440; Height = 220; MinWidth = 340; MinHeight = 200;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; Topmost = true;
            ShowInTaskbar = false; ShowActivated = false;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); Foreground = Brush("#202431"); Background = Brushes.White;
            // Native caption hit testing covers blank space as well as title text.
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 50,
                ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(18), UseAeroCaptionButtons = false });
            frame = new Border { BorderBrush = Brush("#D9FFFFFF"), BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(18) }; Content = frame;
            var grid = new Grid(); frame.Child = grid;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent, ToolTip = "拖动标题栏移动浮窗" };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.Children.Add(header);
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(brand);
            var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Brush("#0A84FF") };
            badge.Child = new TextBlock { Text = "译", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(badge);
            var names = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = "浮译", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#202431") });
            names.Children.Add(new TextBlock { Text = screenshot == null ? "划词翻译" : "截图翻译", FontSize = 9, Foreground = Brush("#8794AA"), Margin = new Thickness(0, 1, 0, 0) }); brand.Children.Add(names);
            pin = IconButton("M4,2 L12,2 L11,8 L13,10 L3,10 L5,8 Z M8,10 L8,15", "取消置顶");
            pin.Foreground = Brush("#0A84FF"); pin.Background = Brush("#120A84FF"); pin.Margin = new Thickness(0, 0, 4, 0); Grid.SetColumn(pin, 1); header.Children.Add(pin);
            pin.Click += delegate { Topmost = !Topmost; pin.Foreground = Brush(Topmost ? "#0A84FF" : "#8391A7"); pin.Background = Topmost ? Brush("#120A84FF") : Brushes.Transparent; pin.ToolTip = Topmost ? "取消置顶" : "置顶"; };
            close = IconButton("M4,4 L12,12 M12,4 L4,12", "关闭"); close.Click += delegate { Close(); }; Grid.SetColumn(close, 2); header.Children.Add(close);
            WindowChrome.SetIsHitTestVisibleInChrome(pin, true); WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            output = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = 17, Foreground = Brush("#202431"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) }; Grid.SetRow(output, 1); grid.Children.Add(output);
            originalPanel = new Border { Background = Brush("#72F0F6FF"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
            originalPanel.Child = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Background = Brushes.Transparent, FontSize = 12, Foreground = Brush("#70829D") }; Grid.SetRow(originalPanel, 2); grid.Children.Add(originalPanel);
            var footer = new StackPanel(); Grid.SetRow(footer, 3); grid.Children.Add(footer);
            progress = new ProgressBar { Height = 2, IsIndeterminate = true, Foreground = Brush("#0A84FF"), Background = Brush("#E3EFFF"), Margin = new Thickness(0, 0, 0, 7) }; footer.Children.Add(progress);
            status = new TextBlock { Text = "正在翻译…", FontSize = 10, Foreground = Brush("#8B98AB"), Margin = new Thickness(0, 0, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }; footer.Children.Add(status);
            var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(buttons);
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; buttons.Children.Add(left);
            showOriginal = TextButton("原文 ⌄"); showOriginal.Foreground = Brush("#7386A2"); showOriginal.Margin = new Thickness(0, 0, 14, 0); showOriginal.Visibility = screenshot == null ? Visibility.Visible : Visibility.Collapsed;
            showOriginal.Click += delegate { originalExpanded = !originalExpanded; originalPanel.Visibility = originalExpanded ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)showOriginal.Content).Text = originalExpanded ? "原文 ⌃" : "原文 ⌄"; FitContent(); }; left.Children.Add(showOriginal);
            var main = TextButton("设置"); main.Click += delegate { showSettings(); }; left.Children.Add(main);
            autoSize = TextButton("自适应"); autoSize.Margin = new Thickness(14, 0, 0, 0); autoSize.Visibility = Visibility.Collapsed;
            autoSize.ToolTip = "恢复按文本长度自动调整窗口大小";
            autoSize.Click += delegate { RestoreAutomaticSize(); }; left.Children.Add(autoSize);
            retry = TextButton("重试"); retry.Margin = new Thickness(0, 0, 12, 0); retry.Visibility = Visibility.Collapsed;
            retry.Click += async delegate { await Translate(); }; Grid.SetColumn(retry, 1); buttons.Children.Add(retry);
            copy = new Button { Style = (Style)Application.Current.FindResource("PopupPrimaryButton"), Content = ButtonLabel("复制译文", 11), Height = 30, Padding = new Thickness(13, 0, 13, 0), IsEnabled = false };
            copy.Click += delegate { try { Clipboard.SetText(output.Text); status.Text = "已复制"; } catch { status.Text = "剪贴板正忙，请稍后重试。"; } }; Grid.SetColumn(copy, 2); buttons.Children.Add(copy);
            var grip = new ResizeGrip { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -10, -10),
                Style = (Style)Application.Current.FindResource("PopupResizeGrip"), Cursor = Cursors.SizeNWSE,
                ToolTip = "拖动这里或窗口边缘调整大小" };
            WindowChrome.SetResizeGripDirection(grip, ResizeGripDirection.BottomRight); Grid.SetRowSpan(grip, 4); grid.Children.Add(grip);
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            ApplyAppearance(settings); FitContent(); SourceInitialized += delegate {
                nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); nativeSource.AddHook(SizeHook);
                ApplyAppearance(settings); FitContent();
            };
            Loaded += delegate { ApplyAppearance(settings); Native.RoundWindow(this); Native.POINT mouse; Native.GetCursorPos(out mouse);
                var work = Forms.Screen.FromPoint(new System.Drawing.Point(mouse.X, mouse.Y)).WorkingArea;
                Native.RECT bounds; Native.GetWindowRect(new WindowInteropHelper(this).Handle, out bounds); int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
                int x = Math.Max(work.Left, Math.Min(mouse.X + 18, work.Right - width)); int y = Math.Max(work.Top, Math.Min(mouse.Y + 20, work.Bottom - height));
                Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); FitContent(); };
            Closed += delegate { closed = true; if (nativeSource != null) nativeSource.RemoveHook(SizeHook); if (cancellation != null) cancellation.Cancel(); image = null; };
        }
        private static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
        private static TextBlock ButtonLabel(string text, double size)
        {
            var label = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) }); return label;
        }
        private static Button IconButton(string geometry, string hint)
        {
            var icon = new Path { Data = Geometry.Parse(geometry), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            icon.SetBinding(Path.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            return new Button { Content = icon, Width = 28, Height = 28, Padding = new Thickness(0), Foreground = Brush("#8794A8"), Style = (Style)Application.Current.FindResource("PopupIconButton"), ToolTip = hint };
        }
        private static Button TextButton(string text) { return new Button { Content = ButtonLabel(text, 11), Style = (Style)Application.Current.FindResource("PopupTextButton"), Height = 28, Padding = new Thickness(0), Foreground = Brush("#0A84FF") }; }
        private IntPtr SizeHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0214) { userSized = true; autoSize.Visibility = Visibility.Visible; }
            if (message == 0x0232 || message == 0x02E0 || message == 0x007E)
                Dispatcher.BeginInvoke(new Action(delegate { if (!closed) FitContent(); }));
            return IntPtr.Zero;
        }
        public void RestoreAutomaticSize() { userSized = false; autoSize.Visibility = Visibility.Collapsed; FitContent(); }
        public async Task Translate()
        {
            if (closed) return;
            if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); } cancellation = new CancellationTokenSource();
            copy.IsEnabled = false; retry.Visibility = Visibility.Collapsed; progress.Visibility = Visibility.Visible; output.Text = ""; output.FontSize = 17; status.Text = "正在翻译为" + settings.TargetLanguage + "…"; FitContent();
            var watch = Stopwatch.StartNew();
            try { string result = await api.TranslateAsync(settings, original, image, cancellation.Token); if (closed) return;
                output.Text = result; copy.IsEnabled = true; string model = image != null && !string.IsNullOrWhiteSpace(settings.VisionModel) ? settings.VisionModel : settings.Model;
                status.Text = settings.TargetLanguage + " · " + watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
                if (Completed != null) Completed(new HistoryEntry { Source = original, Translation = result, Model = model, Target = settings.TargetLanguage, Time = DateTime.Now }); }
            catch (OperationCanceledException) { if (!closed) status.Text = "翻译已取消"; }
            catch (Exception error) { if (!closed) { status.Text = "翻译未完成"; output.Text = error.Message; output.FontSize = 14; retry.Visibility = Visibility.Visible; } }
            finally { if (!closed) { progress.Visibility = Visibility.Collapsed; FitContent(); } }
        }
        public void ApplyAppearance(Settings config)
        {
            bool initialized = new WindowInteropHelper(this).Handle != IntPtr.Zero;
            bool acrylic = initialized ? Native.SetAcrylic(this, config.GlassEffect) : config.GlassEffect;
            // A bright local tint keeps the popup readable over dark desktops.
            double transparency = Math.Max(15, Math.Min(70, config.GlassTransparency));
            byte alpha = acrylic ? (byte)Math.Round(255 * (0.96 - transparency * 0.0018)) : (byte)255;
            var surface = new LinearGradientBrush(); surface.StartPoint = new Point(0, 0); surface.EndPoint = new Point(1, 1);
            surface.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 255, 255, 255), 0)); surface.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 245, 250, 255), 1)); frame.Background = surface;
        }
        private void FitContent()
        {
            Size available = AvailableSize();
            MinWidth = Math.Min(340, available.Width); MinHeight = Math.Min(200, available.Height);
            MaxWidth = available.Width; MaxHeight = available.Height;
            if (!userSized) {
                string visibleText = string.IsNullOrEmpty(output.Text) ? (image == null ? original : "") : output.Text;
                double desiredWidth = Math.Max(PreferredWidth(original), PreferredWidth(visibleText));
                if (image != null) desiredWidth = Math.Max(440, desiredWidth);
                Width = Math.Max(MinWidth, Math.Min(available.Width, desiredWidth));
                var text = new TextBlock { Text = visibleText, FontFamily = output.FontFamily, FontSize = output.FontSize, TextWrapping = TextWrapping.Wrap };
                // Match the text viewport, including room for a scrollbar, and
                // measure paragraphs rather than guessing height from raw count.
                text.Measure(new Size(Math.Max(1, Width - 56), double.PositiveInfinity));
                if (originalExpanded) originalPanel.Measure(new Size(Math.Max(1, Width - 38), double.PositiveInfinity));
                double desiredHeight = text.DesiredSize.Height + 158 + (originalExpanded ? originalPanel.DesiredSize.Height : 0);
                Height = Math.Max(Math.Min(220, available.Height), Math.Min(Math.Min(820, available.Height), desiredHeight));
            }
            if (new WindowInteropHelper(this).Handle != IntPtr.Zero) Dispatcher.BeginInvoke(new Action(delegate { if (closed) return;
                IntPtr handle = new WindowInteropHelper(this).Handle; Native.RECT rect; Native.GetWindowRect(handle, out rect);
                var work = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)).WorkingArea;
                int x = Math.Max(work.Left, Math.Min(rect.Left, work.Right - (rect.Right - rect.Left))); int y = Math.Max(work.Top, Math.Min(rect.Top, work.Bottom - (rect.Bottom - rect.Top)));
                Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); }));
        }
        public static double PreferredWidth(string value)
        {
            double units = 0;
            foreach (char character in value ?? "") units += character == '\n' ? 16 : (character <= 0x7f ? .55 : 1);
            return units <= 80 ? 400 : units <= 300 ? 500 : units <= 900 ? 620 : 740;
        }
        private Size AvailableSize()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle; Native.POINT mouse; Native.GetCursorPos(out mouse);
            var screen = handle == IntPtr.Zero ? Forms.Screen.FromPoint(new System.Drawing.Point(mouse.X, mouse.Y)) : Forms.Screen.FromHandle(handle);
            double scale = Native.DisplayScale(handle, mouse);
            return new Size(Math.Max(240, screen.WorkingArea.Width / scale - 32), Math.Max(160, screen.WorkingArea.Height / scale - 32));
        }
        public int NativeHitTest(Point point) { Point screen = PointToScreen(point); int packed = ((int)screen.X & 0xffff) | (((int)screen.Y & 0xffff) << 16); return Native.SendMessage(new WindowInteropHelper(this).Handle, 0x0084, IntPtr.Zero, new IntPtr(packed)).ToInt32(); }
        public Point CloseButtonCenter() { return close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), this); }
        public void ShowNotice(string message) { originalPanel.Visibility = Visibility.Collapsed; showOriginal.Visibility = Visibility.Collapsed; output.Text = message; output.FontSize = 14; progress.Visibility = Visibility.Collapsed; status.Text = "需要设置"; copy.Visibility = Visibility.Collapsed; FitContent(); }
        public void PreviewResult(string result) { output.Text = result; copy.IsEnabled = true; progress.Visibility = Visibility.Collapsed; status.Text = "界面预览 · 示例译文"; FitContent(); }
    }
}
