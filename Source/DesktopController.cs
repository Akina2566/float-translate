using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace FloatTranslate
{
    public sealed class DesktopController : IDisposable
    {
        public readonly Window Window;
        private readonly Store store;
        private readonly ApiClient api = new ApiClient();
        private readonly bool preview;
        private HotkeyManager shortcuts;
        private Forms.NotifyIcon tray;
        private CancellationTokenSource mainCancellation;
        private PopupWindow popup;
        private bool quitting, selecting, loadingLanguages, loadingAppearance;
        public DesktopController(Store data, bool previewMode)
        {
            store = data; preview = previewMode;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) if (stream != null) Window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Wire(); LoadSettings(); LoadLanguages(); RefreshStatus(); ApplyAppearance(store.Current);
            Window.SourceInitialized += delegate
            {
                Native.RoundWindow(Window);
                ApplyAppearance(store.Current);
                if (preview) return;
                shortcuts = new HotkeyManager(Window); shortcuts.Triggered += async delegate(bool capture) { await GlobalTranslate(capture); };
                try { shortcuts.Apply(store.Current.SelectionShortcut, store.Current.CaptureShortcut); }
                catch (Exception e) { string message = e.Message; Window.Dispatcher.BeginInvoke(new Action(delegate { F<TextBlock>("SettingsStatus").Text = message; F<TextBlock>("SidebarStatus").Text = "快捷键需要设置"; ShowNotice(message + " 点击设置进行调整。"); })); }
            };
            Window.Loaded += delegate { ApplyAppearance(store.Current); };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!quitting && !preview) { e.Cancel = true; Window.Hide(); } };
            if (!preview) CreateTray();
            if (!string.IsNullOrEmpty(store.Warning)) F<TextBlock>("TranslateStatus").Text = store.Warning;
        }
        private T F<T>(string name) where T : class { return (T)Window.FindName(name); }
        private void Wire()
        {
            F<Button>("NavTranslate").Click += delegate { SetPage("Translate"); };
            F<Button>("NavHistory").Click += delegate { SetPage("History"); RenderHistory(); };
            F<Button>("NavSettings").Click += delegate { SetPage("Settings"); };
            F<Button>("MinimizeButton").Click += delegate { Window.WindowState = WindowState.Minimized; };
            F<Button>("HideButton").Click += delegate { Window.Hide(); };
            F<Button>("TranslateButton").Click += async delegate { await TranslateMain(); };
            F<Button>("CaptureButton").Click += async delegate { await GlobalTranslate(true); };
            F<Button>("CancelTranslate").Click += delegate { if (mainCancellation != null) mainCancellation.Cancel(); };
            F<Button>("ClearInput").Click += delegate { F<TextBox>("InputText").Clear(); };
            F<TextBox>("InputText").TextChanged += delegate
            {
                string text = F<TextBox>("InputText").Text;
                F<TextBlock>("InputPlaceholder").Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                F<TextBlock>("CharacterCount").Text = text.Length + " 字";
            };
            F<TextBox>("ResultText").TextChanged += delegate { F<StackPanel>("ResultPlaceholder").Visibility = F<TextBox>("ResultText").Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            F<Button>("CopyResult").Click += delegate
            {
                try { Clipboard.SetText(F<TextBox>("ResultText").Text); F<TextBlock>("TranslateStatus").Text = "译文已复制"; }
                catch { F<TextBlock>("TranslateStatus").Text = "剪贴板正忙，请稍后重试。"; }
            };
            Window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && F<Grid>("TranslatePage").Visibility == Visibility.Visible) { e.Handled = true; await TranslateMain(); }
            };
            F<Button>("SwapLanguages").Click += delegate
            {
                string source = Convert.ToString(F<ComboBox>("SourceLanguage").SelectedItem), target = Convert.ToString(F<ComboBox>("TargetLanguage").SelectedItem);
                loadingLanguages = true;
                F<ComboBox>("SourceLanguage").SelectedItem = source == "自动检测" ? "自动检测" : target;
                F<ComboBox>("TargetLanguage").SelectedItem = source == "自动检测" ? (target == "简体中文" ? "英语" : "简体中文") : source;
                loadingLanguages = false; SaveLanguages();
            };
            F<ComboBox>("SourceLanguage").SelectionChanged += delegate { SaveLanguages(); };
            F<ComboBox>("TargetLanguage").SelectionChanged += delegate { SaveLanguages(); };
            F<Button>("SaveSettings").Click += delegate { ApplySettings(); };
            F<CheckBox>("GlassEnabled").Checked += delegate { PreviewAppearance(); };
            F<CheckBox>("GlassEnabled").Unchecked += delegate { PreviewAppearance(); };
            F<Slider>("GlassTransparency").ValueChanged += delegate { PreviewAppearance(); };
            F<Button>("TestApi").Click += async delegate { await TestConnection(); };
            F<CheckBox>("RevealKey").Checked += delegate { F<TextBox>("ApiKeyVisible").Text = F<PasswordBox>("ApiKey").Password; F<PasswordBox>("ApiKey").Visibility = Visibility.Collapsed; F<TextBox>("ApiKeyVisible").Visibility = Visibility.Visible; };
            F<CheckBox>("RevealKey").Unchecked += delegate { F<PasswordBox>("ApiKey").Password = F<TextBox>("ApiKeyVisible").Text; F<TextBox>("ApiKeyVisible").Clear(); F<PasswordBox>("ApiKey").Visibility = Visibility.Visible; F<TextBox>("ApiKeyVisible").Visibility = Visibility.Collapsed; };
            F<Button>("ResetShortcuts").Click += delegate { F<TextBox>("SelectionShortcut").Text = "Ctrl + Alt + T"; F<TextBox>("CaptureShortcut").Text = "Ctrl + Alt + S"; };
            foreach (string name in new[] { "SelectionShortcut", "CaptureShortcut" })
            {
                var field = F<TextBox>(name);
                field.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Tab) return;
                    e.Handled = true; string text = Hotkey.FromEvent(e);
                    if (text != null) ((TextBox)sender).Text = text;
                };
                field.GotKeyboardFocus += delegate { if (shortcuts != null) shortcuts.Suspend(); F<TextBlock>("SettingsStatus").Text = "按下新的组合键；Tab 移到下一个输入框。"; };
                field.LostKeyboardFocus += delegate { if (shortcuts != null) try { shortcuts.Resume(); } catch (Exception e) { F<TextBlock>("SettingsStatus").Text = e.Message; } };
            }
            F<Button>("ClearHistory").Click += delegate
            {
                if (store.History.Count == 0) return;
                if (MessageBox.Show(Window, "清空这台电脑上保存的所有翻译记录？", "清空记录", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                try { store.ClearHistory(); RenderHistory(); } catch (Exception e) { MessageBox.Show(Window, "记录无法清空：" + e.Message, "浮译"); }
            };
        }
        private void LoadLanguages()
        {
            loadingLanguages = true;
            string[] languages = { "简体中文", "繁体中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语", "葡萄牙语", "意大利语", "阿拉伯语" };
            F<ComboBox>("SourceLanguage").Items.Add("自动检测");
            foreach (string language in languages) { F<ComboBox>("SourceLanguage").Items.Add(language); F<ComboBox>("TargetLanguage").Items.Add(language); }
            F<ComboBox>("SourceLanguage").SelectedItem = store.Current.SourceLanguage;
            if (F<ComboBox>("SourceLanguage").SelectedIndex < 0) F<ComboBox>("SourceLanguage").SelectedIndex = 0;
            F<ComboBox>("TargetLanguage").SelectedItem = store.Current.TargetLanguage;
            if (F<ComboBox>("TargetLanguage").SelectedIndex < 0) F<ComboBox>("TargetLanguage").SelectedIndex = 0;
            loadingLanguages = false;
        }
        private void SaveLanguages()
        {
            if (loadingLanguages || F<ComboBox>("TargetLanguage").SelectedItem == null || F<ComboBox>("SourceLanguage").SelectedItem == null) return;
            Settings current = store.Current.Copy(); current.SourceLanguage = Convert.ToString(F<ComboBox>("SourceLanguage").SelectedItem); current.TargetLanguage = Convert.ToString(F<ComboBox>("TargetLanguage").SelectedItem);
            if (!preview) try { store.SaveSettings(current); } catch { F<TextBlock>("TranslateStatus").Text = "语言偏好无法保存，请检查本地数据目录权限。"; }
            else store.Current = current;
        }
        private void LoadSettings()
        {
            loadingAppearance = true;
            Settings config = store.Current;
            F<TextBox>("ApiEndpoint").Text = config.Endpoint; F<PasswordBox>("ApiKey").Password = config.ApiKey;
            F<TextBox>("TextModel").Text = config.Model; F<TextBox>("VisionModel").Text = config.VisionModel;
            F<TextBox>("TranslationPrompt").Text = config.Prompt;
            F<TextBox>("SelectionShortcut").Text = config.SelectionShortcut; F<TextBox>("CaptureShortcut").Text = config.CaptureShortcut;
            F<CheckBox>("KeepHistory").IsChecked = config.KeepHistory; F<CheckBox>("StartWithWindows").IsChecked = config.StartWithWindows;
            F<CheckBox>("GlassEnabled").IsChecked = config.GlassEffect; F<Slider>("GlassTransparency").Value = config.GlassTransparency;
            loadingAppearance = false;
        }
        private void PreviewAppearance()
        {
            if (loadingAppearance) return;
            Settings config = store.Current.Copy(); config.GlassEffect = F<CheckBox>("GlassEnabled").IsChecked == true; config.GlassTransparency = F<Slider>("GlassTransparency").Value;
            ApplyAppearance(config); if (popup != null) popup.ApplyAppearance(config);
            F<TextBlock>("SettingsStatus").Text = "外观已预览；保存并应用后记住这个选择。";
        }
        private void ApplyAppearance(Settings config)
        {
            bool acrylic = preview ? config.GlassEffect : Native.SetAcrylic(Window, config.GlassEffect);
            Appearance.ApplyPalette(config, acrylic);
            F<Slider>("GlassTransparency").IsEnabled = config.GlassEffect;
            F<TextBlock>("GlassSupportHint").Text = !config.GlassEffect ? "毛玻璃已关闭，正在使用浅色背景。" : acrylic ? "系统毛玻璃已启用，主窗口与小浮窗同步使用。" : "Windows 11 22H2 及以上支持系统毛玻璃；当前使用浅色背景。";
        }
        private Settings ReadEditor()
        {
            var config = store.Current.Copy();
            config.Endpoint = F<TextBox>("ApiEndpoint").Text.Trim(); config.ApiKey = F<CheckBox>("RevealKey").IsChecked == true ? F<TextBox>("ApiKeyVisible").Text.Trim() : F<PasswordBox>("ApiKey").Password.Trim();
            config.Model = F<TextBox>("TextModel").Text.Trim(); config.VisionModel = F<TextBox>("VisionModel").Text.Trim(); config.Prompt = F<TextBox>("TranslationPrompt").Text.Trim();
            config.SelectionShortcut = F<TextBox>("SelectionShortcut").Text; config.CaptureShortcut = F<TextBox>("CaptureShortcut").Text;
            config.KeepHistory = F<CheckBox>("KeepHistory").IsChecked == true; config.StartWithWindows = F<CheckBox>("StartWithWindows").IsChecked == true;
            config.GlassEffect = F<CheckBox>("GlassEnabled").IsChecked == true; config.GlassTransparency = F<Slider>("GlassTransparency").Value;
            config.Validate(); return config;
        }
        private void ApplySettings()
        {
            Settings old = store.Current.Copy(); bool hotkeysApplied = false, startupApplied = false;
            try
            {
                Settings next = ReadEditor();
                if (shortcuts != null) { shortcuts.Apply(next.SelectionShortcut, next.CaptureShortcut); hotkeysApplied = true; }
                if (!preview && next.StartWithWindows != old.StartWithWindows) { Store.SetAutoStart(next.StartWithWindows); startupApplied = true; }
                if (!preview) store.SaveSettings(next); else store.Current = next;
                RefreshStatus(); ApplyAppearance(next); if (popup != null) popup.ApplyAppearance(next); F<TextBlock>("SettingsStatus").Text = "已保存，新的快捷键与外观立即生效。";
                F<TextBlock>("TranslateStatus").Text = "准备好了，输入文字或使用全局快捷键。";
            }
            catch (Exception error)
            {
                string rollback = "";
                if (hotkeysApplied && shortcuts != null) try { shortcuts.Apply(old.SelectionShortcut, old.CaptureShortcut); } catch { rollback = " 原快捷键无法恢复，请重新设置。"; }
                if (startupApplied) try { Store.SetAutoStart(old.StartWithWindows); } catch { rollback += " 登录启动设置无法恢复。"; }
                F<TextBlock>("SettingsStatus").Text = error.Message + rollback;
            }
        }
        private async Task TestConnection()
        {
            F<Button>("TestApi").IsEnabled = false; F<TextBlock>("SettingsStatus").Text = "正在测试连接（会发出一次小额模型请求）…";
            try
            {
                Settings config = ReadEditor(); config.TargetLanguage = "简体中文"; config.SourceLanguage = "英语";
                string response = await api.TranslateAsync(config, "Connection successful.", null, CancellationToken.None);
                F<TextBlock>("SettingsStatus").Text = "连接成功 · 模型返回：" + (response.Length > 80 ? response.Substring(0, 80) : response);
            }
            catch (Exception error) { F<TextBlock>("SettingsStatus").Text = error.Message; }
            finally { F<Button>("TestApi").IsEnabled = true; }
        }
        private void RefreshStatus()
        {
            bool configured = store.Current.IsConfigured;
            F<TextBlock>("SidebarStatus").Text = configured ? "API 已配置" : "尚未配置 API";
            F<System.Windows.Shapes.Ellipse>("StatusDot").Fill = new SolidColorBrush(configured ? Color.FromRgb(48, 181, 118) : Color.FromRgb(163, 173, 187));
            F<TextBlock>("SelectionHint").Text = store.Current.SelectionShortcut; F<TextBlock>("CaptureHint").Text = store.Current.CaptureShortcut;
        }
        private bool EnsureConfigured()
        {
            if (store.Current.IsConfigured) return true;
            ShowMain(); SetPage("Settings"); F<TextBlock>("SettingsStatus").Text = "请填写 API 地址、密钥和文字模型，然后保存并应用。"; return false;
        }
        private async Task TranslateMain()
        {
            if (mainCancellation != null || !EnsureConfigured()) return;
            string text = F<TextBox>("InputText").Text.Trim();
            if (text.Length == 0) { F<TextBlock>("TranslateStatus").Text = "先输入或粘贴需要翻译的文字。"; F<TextBox>("InputText").Focus(); return; }
            if (text.Length > 60000) { F<TextBlock>("TranslateStatus").Text = "文字超过 60,000 字，请分段翻译。"; return; }
            mainCancellation = new CancellationTokenSource(); Settings config = store.Current.Copy(); var watch = Stopwatch.StartNew();
            F<Button>("TranslateButton").IsEnabled = false; F<Button>("CopyResult").IsEnabled = false;
            F<Button>("CancelTranslate").Visibility = Visibility.Visible; F<ProgressBar>("MainProgress").Visibility = Visibility.Visible;
            F<TextBox>("ResultText").Text = ""; F<TextBlock>("TranslateStatus").Text = "正在翻译…"; F<TextBlock>("ResultMeta").Text = config.Model;
            try
            {
                string result = await api.TranslateAsync(config, text, null, mainCancellation.Token);
                F<TextBox>("ResultText").Text = result; F<Button>("CopyResult").IsEnabled = true;
                F<TextBlock>("ResultMeta").Text = config.Model + " · " + watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
                F<TextBlock>("TranslateStatus").Text = "翻译完成";
                Remember(new HistoryEntry { Source = text, Translation = result, Model = config.Model, Target = config.TargetLanguage, Time = DateTime.Now });
            }
            catch (OperationCanceledException) { F<TextBlock>("TranslateStatus").Text = "翻译已取消"; }
            catch (Exception error) { F<TextBlock>("TranslateStatus").Text = error.Message; }
            finally
            {
                mainCancellation.Dispose(); mainCancellation = null;
                F<Button>("TranslateButton").IsEnabled = true; F<Button>("CancelTranslate").Visibility = Visibility.Collapsed; F<ProgressBar>("MainProgress").Visibility = Visibility.Collapsed;
            }
        }
        private async Task GlobalTranslate(bool capture)
        {
            if (selecting) return;
            if (!store.Current.IsConfigured) { ShowNotice("请先连接你的大模型 API。点击下方设置，填写 API 地址、密钥和模型后保存。"); return; }
            selecting = true;
            try
            {
                string text; byte[] screenshot = null;
                if (capture)
                {
                    bool visible = Window.IsVisible && Window.WindowState != WindowState.Minimized;
                    Window.Hide(); if (popup != null) popup.Hide();
                    await Task.Delay(180);
                    var selector = new ScreenshotWindow(); bool? result = selector.ShowDialog();
                    if (result != true) { if (visible) ShowMain(); if (popup != null) popup.Show(); return; }
                    screenshot = selector.ImageBytes; text = "[截图翻译]";
                }
                else
                {
                    text = await SelectionReader.ReadAsync();
                    if (text.Length > 60000) throw new Exception("选中文字超过 60,000 字，请缩小选区。");
                }
                if (popup != null) popup.Close();
                popup = new PopupWindow(store.Current, text, screenshot, api, ShowSettings); PopupWindow current = popup;
                popup.Completed += Remember; popup.Closed += delegate { if (popup == current) popup = null; };
                popup.Show(); Task translation = popup.Translate();
                // Translate handles and reports all request errors inside the floating window.
            }
            catch (Exception error)
            {
                ShowNotice(error.Message);
            }
            finally { selecting = false; }
        }
        private void ShowNotice(string message)
        {
            if (popup != null) popup.Close();
            popup = new PopupWindow(store.Current, "", null, api, ShowSettings); PopupWindow current = popup;
            popup.ShowNotice(message); popup.Closed += delegate { if (popup == current) popup = null; }; popup.Show();
        }
        private void Remember(HistoryEntry entry)
        {
            if (preview) return;
            try { store.Remember(entry); } catch { F<TextBlock>("TranslateStatus").Text = "译文已生成，但记录无法保存。请检查数据目录权限。"; }
        }
        private void RenderHistory()
        {
            StackPanel panel = F<StackPanel>("HistoryItems"); panel.Children.Clear();
            if (store.History.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "还没有翻译记录。\n完成一次翻译后，就可以在这里回看。", Foreground = new SolidColorBrush(Color.FromRgb(143, 155, 173)), FontSize = 16, Margin = new Thickness(4, 60, 0, 0) }); return;
            }
            foreach (HistoryEntry item in store.History)
            {
                HistoryEntry entry = item;
                var border = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(227, 232, 240)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(0, 0, 8, 13) };
                border.SetResourceReference(Border.BackgroundProperty, "CardBrush"); var content = new StackPanel(); border.Child = content;
                content.Children.Add(new TextBlock { Text = entry.Time.ToString("MM-dd HH:mm") + "  ·  " + entry.Target + "  ·  " + entry.Model, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(137, 151, 171)), Margin = new Thickness(0, 0, 0, 10) });
                content.Children.Add(new TextBlock { Text = entry.Source, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(125, 137, 154)), MaxHeight = 42, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10) });
                content.Children.Add(new TextBox { Text = entry.Translation, IsReadOnly = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap, FontSize = 16, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 0) }; content.Children.Add(row);
                var copy = new Button { Content = "复制译文", FontSize = 11, Padding = new Thickness(11, 6, 11, 6) }; row.Children.Add(copy);
                copy.Click += delegate { try { Clipboard.SetText(entry.Translation); copy.Content = "已复制"; } catch { copy.Content = "剪贴板正忙"; } };
                if (entry.Source != "[截图翻译]")
                {
                    var use = new Button { Content = "重用原文", FontSize = 11, Padding = new Thickness(11, 6, 11, 6), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 255)), Margin = new Thickness(6, 0, 0, 0) }; row.Children.Add(use);
                    use.Click += delegate { F<TextBox>("InputText").Text = entry.Source; SetPage("Translate"); };
                }
                panel.Children.Add(border);
            }
        }
        public void SetPage(string page)
        {
            foreach (string name in new[] { "Translate", "History", "Settings" })
            {
                F<Grid>(name + "Page").Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
                F<Button>("Nav" + name).Background = name == page ? new SolidColorBrush(Color.FromRgb(220, 232, 250)) : Brushes.Transparent;
                F<Button>("Nav" + name).Foreground = name == page ? new SolidColorBrush(Color.FromRgb(0, 108, 229)) : new SolidColorBrush(Color.FromRgb(57, 68, 84));
            }
        }
        public void ShowMain()
        {
            Window.Show(); if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal; Window.Activate();
        }
        public void ShowSettings() { SetPage("Settings"); ShowMain(); }
        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "浮译 · 选词与框选翻译", Visible = true };
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) if (stream != null) tray.Icon = new System.Drawing.Icon(stream); else tray.Icon = System.Drawing.SystemIcons.Application;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("框选翻译", null, async delegate { await GlobalTranslate(true); });
            menu.Items.Add("打开设置", null, delegate { ShowSettings(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出浮译", null, delegate { Quit(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowSettings(); };
        }
        public void Quit() { quitting = true; Dispose(); Window.Close(); Application.Current.Shutdown(); }
        public void Dispose()
        {
            if (mainCancellation != null) mainCancellation.Cancel();
            if (popup != null) popup.Close();
            if (shortcuts != null) { shortcuts.Dispose(); shortcuts = null; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            api.Dispose();
        }
        public void RenderPreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            F<TextBox>("InputText").Text = "Good design is as little design as possible.\n\nIt is about making the important things feel effortless.";
            F<TextBox>("ResultText").Text = "好的设计，是尽可能少的设计。\n\n让重要的事情变得轻松自然。";
            F<TextBlock>("ResultMeta").Text = "界面预览 · 示例译文"; F<TextBlock>("TranslateStatus").Text = "这是界面预览，实际翻译需要配置 API。";
            F<Button>("CopyResult").IsEnabled = true;
            SetPage("Translate"); RenderElement((FrameworkElement)Window.Content, 1080, 758, Path.Combine(directory, "preview-translate.png"));
            SetPage("Settings"); RenderElement((FrameworkElement)Window.Content, 1080, 900, Path.Combine(directory, "preview-settings.png"));
            F<ScrollViewer>("SettingsScroll").ScrollToEnd(); RenderElement((FrameworkElement)Window.Content, 1080, 900, Path.Combine(directory, "preview-shortcuts.png"));
            store.History.Add(new HistoryEntry { Source = F<TextBox>("InputText").Text, Translation = F<TextBox>("ResultText").Text, Time = DateTime.Now, Target = "简体中文", Model = "示例记录" });
            SetPage("History"); RenderHistory(); RenderElement((FrameworkElement)Window.Content, 1080, 758, Path.Combine(directory, "preview-history.png"));
            using (var tempApi = new ApiClient())
            {
                var floating = new PopupWindow(new Settings(), "Good design is as little design as possible.", null, tempApi, delegate { }); floating.PreviewResult("好的设计，是尽可能少的设计。");
                RenderElement((FrameworkElement)floating.Content, (int)floating.Width, (int)floating.Height, Path.Combine(directory, "preview-popup.png"));
                var passage = new System.Text.StringBuilder();
                passage.Append("当语言不再成为障碍，阅读就能回到内容本身。\n\n");
                passage.Append("一段较长的译文，需要足够的空间容纳完整的观点、段落与细节。窗口会根据文本长度自动调整宽高，让每一行文字保持舒适的阅读距离。\n\n");
                passage.Append("你可以拖动标题栏，把译文移到顺手的位置；也可以拖动边缘或右下角，按自己的习惯放大、缩小窗口。手动调整后，翻译完成不会改变你选好的尺寸。\n\n");
                passage.Append("如果希望重新交给软件决定大小，点击“自适应”即可。对于超长文本，窗口会控制在屏幕可用空间内，保留全部译文，通过滚动继续阅读。\n\n");
                passage.Append("阅读的节奏由你决定。毛玻璃背景保持轻盈，文字保持清晰，设置窗口继续留在后台。\n\n");
                passage.Append("无论是几句话、一封邮件，还是一页文章，都能获得与内容相称的阅读空间。");
                var longer = new PopupWindow(new Settings(), passage.ToString(), null, tempApi, delegate { }); longer.PreviewResult(passage.ToString());
                RenderElement((FrameworkElement)longer.Content, (int)longer.Width, (int)longer.Height, Path.Combine(directory, "preview-popup-long.png"));
            }
        }
        public static void RenderElement(FrameworkElement element, int width, int height, string path)
        {
            element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
            // Preview only: a soft, synthetic backdrop illustrates translucent surfaces.
            // Native desktop blur is supplied by DWM when the real windows are shown.
            var backdrop = new DrawingVisual();
            using (DrawingContext context = backdrop.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(228, 236, 247)), null, new Rect(0, 0, width, height));
                var blue = new RadialGradientBrush(Color.FromRgb(145, 194, 246), Color.FromArgb(0, 145, 194, 246));
                var violet = new RadialGradientBrush(Color.FromRgb(198, 178, 235), Color.FromArgb(0, 198, 178, 235));
                var mint = new RadialGradientBrush(Color.FromRgb(177, 217, 221), Color.FromArgb(0, 177, 217, 221));
                context.DrawEllipse(blue, null, new Point(width * .15, height * .30), width * .6, height * .6);
                context.DrawEllipse(violet, null, new Point(width * .85, height * .75), width * .5, height * .6);
                context.DrawEllipse(mint, null, new Point(width * .6, height * .15), width * .35, height * .45);
            }
            var composite = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); composite.Render(backdrop);
            var foreground = new DrawingVisual(); using (DrawingContext context = foreground.RenderOpen()) context.DrawImage(bitmap, new Rect(0, 0, width, height)); composite.Render(foreground); bitmap = composite;
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) png.Save(stream);
        }
    }
}
