using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FloatTranslate
{
    public static class TestRunner
    {
        private static readonly List<string> report = new List<string>();
        private static int failures;
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Check(string name, Action test)
        {
            try { test(); report.Add("PASS  " + name); } catch (Exception e) { failures++; report.Add("FAIL  " + name + ": " + e.Message); }
        }
        private static void Throws(Action test, string contains)
        {
            try { test(); } catch (Exception e) { Assert(e.Message.Contains(contains), "Unexpected error: " + e.Message); return; }
            throw new Exception("Expected rejection was not raised.");
        }
        public static int Run(string path)
        {
            Check("Endpoint normalization and complete endpoint", delegate
            {
                Assert(ApiClient.BuildEndpoint("https://example.com/v1/").AbsoluteUri == "https://example.com/v1/chat/completions", "Base URL not normalized");
                Assert(ApiClient.BuildEndpoint("https://example.com/v1/chat/completions/").AbsoluteUri == "https://example.com/v1/chat/completions", "Duplicate suffix");
                Assert(ApiClient.BuildEndpoint("http://127.0.0.1:11434/v1").IsLoopback, "Local API rejected");
            });
            Check("Reject insecure remote API, credentials and query", delegate
            {
                Throws(delegate { ApiClient.BuildEndpoint("http://example.com/v1"); }, "HTTPS");
                Throws(delegate { ApiClient.BuildEndpoint("https://user:secret@example.com/v1"); }, "API 地址");
                Throws(delegate { ApiClient.BuildEndpoint("https://example.com/v1?key=secret"); }, "API 地址");
                Throws(delegate { ApiClient.BuildEndpoint("file:///C:/test"); }, "HTTPS");
            });
            Check("Hotkey parsing, aliases and invalid shortcuts", delegate
            {
                Assert(Hotkey.Parse("Ctrl + Alt + T").KeyCode == 0x54, "Wrong key code");
                Assert(Hotkey.Parse("control + alt + t").Equals(Hotkey.Parse("Ctrl + Alt + T")), "Aliases not equal");
                Assert(Hotkey.Parse("Win + Shift + F9").Modifiers == 12, "Wrong modifiers");
                Assert(Hotkey.Parse("Ctrl + 9").KeyCode == 0x39, "Digit parse");
                Throws(delegate { Hotkey.Parse("Shift + T"); }, "Ctrl");
                Throws(delegate { Hotkey.Parse("Ctrl + T + S"); }, "格式");
                Throws(delegate { Hotkey.Parse("Ctrl + LeftCtrl"); }, "Ctrl");
                var duplicate = new Settings { Model = "test", CaptureShortcut = "ctrl + alt + t" };
                Throws(duplicate.Validate, "不能相同");
            });
            Check("Windows global shortcut registration and conflict detection", delegate
            {
                uint key = 0;
                for (uint candidate = 0x70; candidate <= 0x87; candidate++) if (Native.RegisterHotKey(IntPtr.Zero, 6001, 0x4007, candidate)) { key = candidate; break; }
                Assert(key != 0, "Unable to register a test hotkey");
                try { Assert(!Native.RegisterHotKey(IntPtr.Zero, 6002, 0x4007, key), "Duplicate registration accepted"); }
                finally { Native.UnregisterHotKey(IntPtr.Zero, 6001); Native.UnregisterHotKey(IntPtr.Zero, 6002); }
            });
            Check("DPAPI round trip; key is never stored in plaintext", delegate
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "test-data-" + Guid.NewGuid().ToString("N"));
                var store = new Store(directory); var settings = new Settings { Model = "mock", ApiKey = "test-only-key-不要使用" };
                store.SaveSettings(settings); store.SaveSettings(settings);
                string json = File.ReadAllText(Path.Combine(directory, "settings.json"));
                Assert(!json.Contains(settings.ApiKey) && !json.Contains("test-only-key"), "Plaintext key persisted");
                Assert(new Store(directory).Current.ApiKey == settings.ApiKey, "Key did not decrypt");
                Directory.Delete(directory, true);
            });
            Check("History retention, Unicode, disabled history and clear", delegate
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "test-data-" + Guid.NewGuid().ToString("N"));
                var store = new Store(directory);
                for (int i = 0; i < 105; i++) store.Remember(new HistoryEntry { Source = "原文 " + i, Translation = "译文 " + i, Target = "简体中文", Time = DateTime.Now });
                Assert(store.History.Count == 100 && store.History[0].Translation == "译文 104", "History order or limit");
                Assert(new Store(directory).History[0].Translation == "译文 104", "Unicode persistence");
                store.Current.KeepHistory = false; store.Remember(new HistoryEntry { Translation = "not saved" }); Assert(store.History.Count == 100, "Disabled history still saves");
                store.ClearHistory(); Assert(new Store(directory).History.Count == 0, "Clear failed");
                Directory.Delete(directory, true);
            });
            Check("Screenshot selection maps DIP coordinates to physical pixels", delegate
            {
                Rectangle rectangle = ScreenshotWindow.MapToPixels(new System.Windows.Rect(100, 50, 200, 100), 1280, 720, 1920, 1080);
                Assert(rectangle == new Rectangle(150, 75, 300, 150), "150% scale mapping failed");
                rectangle = ScreenshotWindow.MapToPixels(new System.Windows.Rect(0, 0, 1280, 720), 1280, 720, 1920, 1080);
                Assert(rectangle == new Rectangle(0, 0, 1920, 1080), "Full desktop mapping failed");
            });
            Check("Text request, UTF-8 payload, auth and parsed translation", delegate
            {
                string result = MockCall("Hello 世界", null, 200, "{\"choices\":[{\"message\":{\"content\":\"你好，世界。\"}}]}", delegate(Request request)
                {
                    Assert(request.Headers.Contains("Bearer mock-test-key"), "Auth header missing");
                    Assert(request.Path == "/v1/chat/completions", "Wrong request path");
                    var payload = Parse(request.Body); Assert((string)payload["model"] == "text-model", "Wrong model");
                    var messages = (IList)payload["messages"];
                    Assert((string)((Dictionary<string, object>)messages[1])["content"] == "Hello 世界", "Unicode lost");
                    Assert(((string)((Dictionary<string, object>)messages[0])["content"]).Contains("简体中文"), "Target language missing");
                });
                Assert(result == "你好，世界。", "Wrong translation");
            });
            Check("Screenshot request uses vision model and PNG base64", delegate
            {
                byte[] png;
                using (var bitmap = new Bitmap(2, 2)) using (var stream = new MemoryStream()) { bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); png = stream.ToArray(); }
                string result = MockCall("[截图翻译]", png, 200, "{\"choices\":[{\"message\":{\"content\":\"屏幕译文\"}}]}", delegate(Request request)
                {
                    var payload = Parse(request.Body); Assert((string)payload["model"] == "vision-model", "Wrong vision model");
                    var messages = (IList)payload["messages"]; var content = (IList)((Dictionary<string, object>)messages[1])["content"];
                    var part = (Dictionary<string, object>)content[1]; var image = (Dictionary<string, object>)part["image_url"];
                    Assert((string)image["url"] == "data:image/png;base64," + Convert.ToBase64String(png), "Image bytes changed");
                });
                Assert(result == "屏幕译文", "Image result parse");
            });
            Check("Structured text content response", delegate
            {
                Assert(MockCall("hello", null, 200, "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"你\"},{\"type\":\"text\",\"text\":\"好\"}]}}]}", null) == "你好", "Segmented content parse");
            });
            Check("API errors redact keys and explain authentication failure", delegate
            {
                Throws(delegate { MockCall("hello", null, 401, "{\"error\":{\"message\":\"Invalid mock-test-key\"}}", null); }, "[密钥已隐藏]");
                Throws(delegate { MockCall("hello", null, 429, "{}", null); }, "额度不足");
            });
            Check("Reject malformed / empty response and redirects", delegate
            {
                Throws(delegate { MockCall("hello", null, 200, "{}", null); }, "可读译文");
                Throws(delegate { MockCall("hello", null, 200, "{\"choices\":[{\"message\":{\"content\":null}}]}", null); }, "可读译文");
                Throws(delegate { MockCall("hello", null, 302, "{}", null); }, "重定向");
            });
            Check("Cancellation interrupts an in-flight API request", delegate
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var received = new TaskCompletionSource<bool>();
                var server = Task.Run(async delegate
                {
                    using (TcpClient connection = await listener.AcceptTcpClientAsync()) { ReadRequest(connection.GetStream()); received.SetResult(true); await Task.Delay(200); }
                });
                using (var client = new ApiClient()) using (var cancel = new CancellationTokenSource())
                {
                    Task<string> pending = client.TranslateAsync(new Settings { Endpoint = "http://127.0.0.1:" + port + "/v1", Model = "mock" }, "test", null, cancel.Token);
                    Assert(received.Task.Wait(5000), "Server did not receive request"); cancel.Cancel(); bool cancelled = false;
                    try { pending.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled, "Request was not cancelled");
                }
                server.GetAwaiter().GetResult(); listener.Stop();
            });
            report.Add("\n" + (report.Count - failures) + " passed, " + failures + " failed. " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            File.WriteAllLines(path, report, new UTF8Encoding(false)); return failures == 0 ? 0 : 1;
        }
        private sealed class Request { public string Path, Headers, Body; }
        private static Dictionary<string, object> Parse(string json) { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json); }
        public static async Task<int> RunUiTest(string path)
        {
            var lines = new List<string>();
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "ui-test-data-" + Guid.NewGuid().ToString("N"));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var server = Task.Run(async delegate
                {
                    for (int i = 0; i < 2; i++)
                    {
                        using (TcpClient connection = await listener.AcceptTcpClientAsync())
                        {
                            var stream = connection.GetStream(); Request request = ReadRequest(stream); var payload = Parse(request.Body);
                            Assert((string)payload["model"] == (i == 0 ? "ui-text-model" : "ui-vision-model"), "UI did not apply the configured model");
                            string value = i == 0 ? "你好，世界。" : "框选图片的译文。";
                            string response = new JavaScriptSerializer().Serialize(new { choices = new[] { new { message = new { content = value } } } });
                            byte[] body = Encoding.UTF8.GetBytes(response);
                            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                            await stream.WriteAsync(header, 0, header.Length); await stream.WriteAsync(body, 0, body.Length);
                        }
                    }
                });
                var store = new Store(directory);
                store.Current.Endpoint = "http://127.0.0.1:" + port + "/v1"; store.Current.Model = "initial-model";
                // Reserve uncommon keys only while this hidden test window exists.
                store.Current.SelectionShortcut = "Ctrl + Alt + Shift + F17"; store.Current.CaptureShortcut = "Ctrl + Alt + Shift + F18";
                using (var controller = new DesktopController(store, false))
                {
                    var window = controller.Window; window.Opacity = 0; window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
                    ((System.Windows.Controls.Button)window.FindName("NavSettings")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(((System.Windows.Controls.Grid)window.FindName("SettingsPage")).Visibility == System.Windows.Visibility.Visible, "Settings navigation failed");
                    ((System.Windows.Controls.TextBox)window.FindName("TextModel")).Text = "ui-text-model";
                    ((System.Windows.Controls.TextBox)window.FindName("VisionModel")).Text = "ui-vision-model";
                    ((System.Windows.Controls.Button)window.FindName("SaveSettings")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(store.Current.Model == "ui-text-model", "Save button did not persist settings");
                    lines.Add("PASS  Actual WPF settings navigation, Save button, disk persistence and hotkey registration");
                    ((System.Windows.Controls.Slider)window.FindName("GlassTransparency")).Value = 60;
                    ((System.Windows.Controls.Button)window.FindName("SaveSettings")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(store.Current.GlassEffect && store.Current.GlassTransparency == 60, "Glass settings were not saved");
                    bool nativeGlass = Native.SetAcrylic(window, true);
                    lines.Add("PASS  Glass switch / slider persistence and native acrylic API (supported=" + nativeGlass + ")");
                    ((System.Windows.Controls.Button)window.FindName("NavTranslate")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    ((System.Windows.Controls.TextBox)window.FindName("InputText")).Text = "Hello, world.";
                    var translate = (System.Windows.Controls.Button)window.FindName("TranslateButton");
                    translate.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    for (int i = 0; i < 200 && !translate.IsEnabled; i++) await Task.Delay(25);
                    string output = ((System.Windows.Controls.TextBox)window.FindName("ResultText")).Text;
                    Assert(output == "你好，世界。", "Manual translation failed: " + ((System.Windows.Controls.TextBlock)window.FindName("TranslateStatus")).Text);
                    Assert(store.History.Count == 1, "Manual history was not saved");
                    Assert(((System.Windows.Controls.Button)window.FindName("CopyResult")).IsEnabled, "Copy button still disabled");
                    lines.Add("PASS  Actual Translate button -> HTTP API -> result display -> history and copy state");
                    ((System.Windows.Controls.Button)window.FindName("NavHistory")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(((System.Windows.Controls.StackPanel)window.FindName("HistoryItems")).Children.Count == 1, "History card missing");
                    lines.Add("PASS  Actual history page renders saved translation");
                    byte[] png;
                    using (var bitmap = new Bitmap(12, 12)) using (var stream = new MemoryStream()) { bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); png = stream.ToArray(); }
                    using (var client = new ApiClient())
                    {
                        var floating = new PopupWindow(store.Current, "[截图翻译]", png, client, delegate { }); floating.Opacity = 0;
                        floating.Completed += store.Remember; floating.Show(); await floating.Translate();
                        Assert(store.History.Count == 2 && store.History[0].Translation == "框选图片的译文。", "Floating translation did not complete");
                        floating.UpdateLayout();
                        int captionHit = floating.NativeHitTest(new System.Windows.Point(170, 28));
                        Assert(captionHit == 2, "Blank title strip must return native HTCAPTION, got " + captionHit);
                        Assert(floating.NativeHitTest(new System.Windows.Point(40, 85)) == 1, "Result text must remain selectable, not a caption");
                        Assert(floating.NativeHitTest(floating.CloseButtonCenter()) == 1, "Close control became a drag region");
                        var resultBox = (System.Windows.Controls.TextBox)typeof(PopupWindow).GetField("output", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(floating);
                        Assert(resultBox.ActualHeight >= resultBox.FontSize * 1.4, "Compact popup clips a line of translation");
                        lines.Add("PASS  Native caption hit testing: blank header drags; close and text remain interactive");
                        Assert(floating.NativeHitTest(new System.Windows.Point(floating.ActualWidth - 2, floating.ActualHeight / 2)) == 11, "Right edge must return native resize hit");
                        Assert(floating.NativeHitTest(new System.Windows.Point(floating.ActualWidth / 2, floating.ActualHeight - 2)) == 15, "Bottom edge must return native resize hit");
                        Assert(floating.NativeHitTest(new System.Windows.Point(floating.ActualWidth - 2, floating.ActualHeight - 2)) == 17, "Bottom-right corner must resize");
                        lines.Add("PASS  Native edge and corner hit testing supports manual window resizing");
                        var surface = ((System.Windows.Controls.Border)floating.Content).Background as System.Windows.Media.LinearGradientBrush;
                        Assert(surface != null && surface.GradientStops[0].Color.R == 255 && surface.GradientStops[0].Color.A >= 210, "Glass tint is not the bright readable surface");
                        floating.ApplyAppearance(new Settings { GlassEffect = false });
                        surface = ((System.Windows.Controls.Border)floating.Content).Background as System.Windows.Media.LinearGradientBrush;
                        Assert(surface.GradientStops[0].Color.A == 255, "Glass fallback must be fully opaque and bright");
                        lines.Add("PASS  Bright local glass tint and opaque fallback are independent of the main palette");
                        floating.Close();
                    }
                    lines.Add("PASS  Actual floating window -> vision model -> PNG payload -> completion and local history");
                    Assert(!File.ReadAllText(Path.Combine(directory, "history.json")).Contains("base64"), "Image saved to history");
                    lines.Add("PASS  History does not persist screenshot data");
                    using (var sizingClient = new ApiClient())
                    {
                        var compact = new PopupWindow(store.Current, "Hello", null, sizingClient, delegate { }); compact.Opacity = 0; compact.Show(); compact.PreviewResult("你好。"); compact.UpdateLayout();
                        double compactWidth = compact.Width, compactHeight = compact.Height; compact.Close();
                        string sourceText = new string('原', 650);
                        var reading = new PopupWindow(store.Current, sourceText, null, sizingClient, delegate { }); reading.Opacity = 0;
                        Assert(reading.Width > compactWidth && reading.Height > compactHeight, "Original length did not size the loading popup");
                        reading.Show(); reading.PreviewResult(new string('译', 800)); reading.UpdateLayout();
                        Assert(reading.Width > compactWidth && reading.Height > compactHeight, "Long translation did not grow the popup");
                        Assert(reading.Width <= reading.MaxWidth && reading.Height <= reading.MaxHeight, "Automatic size exceeds screen bounds");
                        lines.Add("PASS  Original and translated character counts grow both dimensions within screen bounds");
                        string hugeTranslation = new string('译', 15000); reading.PreviewResult(hugeTranslation); reading.UpdateLayout();
                        var box = (System.Windows.Controls.TextBox)typeof(PopupWindow).GetField("output", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(reading);
                        var scroll = box.Template.FindName("PART_ContentHost", box) as System.Windows.Controls.ScrollViewer;
                        Assert(box.Text == hugeTranslation && scroll != null && scroll.ExtentHeight > scroll.ViewportHeight, "Large translation must remain complete and scrollable");
                        Assert(reading.Height <= Math.Min(820, reading.MaxHeight), "Huge text is not height-bounded");
                        lines.Add("PASS  Huge translations retain all characters and scroll at the screen-dependent height limit");
                        double chosenWidth = Math.Min(540, reading.MaxWidth), chosenHeight = Math.Min(480, reading.MaxHeight);
                        reading.Width = chosenWidth; reading.Height = chosenHeight; reading.UpdateLayout();
                        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(reading).Handle;
                        Native.RECT rect; Native.GetWindowRect(handle, out rect);
                        IntPtr rectMemory = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RECT)));
                        try {
                            System.Runtime.InteropServices.Marshal.StructureToPtr(rect, rectMemory, false);
                            Native.SendMessage(handle, 0x0214, new IntPtr(8), rectMemory);
                        } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(rectMemory); }
                        reading.PreviewResult(hugeTranslation);
                        Assert(Math.Abs(reading.Width - chosenWidth) < .1 && Math.Abs(reading.Height - chosenHeight) < .1, "Translation completion overwrote manual size");
                        var autoButton = (System.Windows.Controls.Button)typeof(PopupWindow).GetField("autoSize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(reading);
                        Assert(autoButton.Visibility == System.Windows.Visibility.Visible, "Restore automatic size action is missing");
                        autoButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(autoButton.Visibility == System.Windows.Visibility.Collapsed && reading.Width >= chosenWidth && reading.Height >= chosenHeight, "Automatic sizing was not restored");
                        lines.Add("PASS  Manual size survives new text; the adaptive-size button restores automatic dimensions");
                        reading.Close();
                    }
                    window.Hide(); store.Current.Model = "";
                    var method = typeof(DesktopController).GetMethod("GlobalTranslate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    await (Task)method.Invoke(controller, new object[] { false });
                    Assert(!window.IsVisible, "Global translation opened the full app when configuration was missing");
                    var floatingNotice = (PopupWindow)typeof(DesktopController).GetField("popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(controller);
                    Assert(floatingNotice != null && floatingNotice.Width == 400 && floatingNotice.Height <= 280 && !floatingNotice.ShowActivated, "Notice is not a compact non-activating popup");
                    lines.Add("PASS  Global shortcut with missing configuration opens only a compact popup, never the main app");
                    controller.Quit();
                }
                await server;
                lines.Add("\n13 UI workflow checks passed. No real API credentials or third-party services used.");
                File.WriteAllLines(path, lines, new UTF8Encoding(false)); return 0;
            }
            catch (Exception error) { lines.Add("FAIL  " + error.ToString()); File.WriteAllLines(path, lines, new UTF8Encoding(false)); return 1; }
            finally { listener.Stop(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static Request ReadRequest(NetworkStream stream)
        {
            stream.ReadTimeout = 5000;
            var header = new StringBuilder();
            while (header.Length < 32000)
            {
                int value = stream.ReadByte(); if (value < 0) throw new Exception("Client disconnected"); header.Append((char)value);
                if (header.ToString().EndsWith("\r\n\r\n")) break;
            }
            string headers = header.ToString(); int length = 0;
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
            byte[] body = new byte[length]; int offset = 0;
            while (offset < length) { int count = stream.Read(body, offset, length - offset); if (count == 0) throw new Exception("Incomplete body"); offset += count; }
            return new Request { Path = headers.Split(' ')[1], Headers = headers, Body = Encoding.UTF8.GetString(body) };
        }
        private static string MockCall(string text, byte[] image, int status, string response, Action<Request> inspect)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async delegate
            {
                using (TcpClient connection = await listener.AcceptTcpClientAsync())
                {
                    var stream = connection.GetStream(); Request request = ReadRequest(stream); if (inspect != null) inspect(request);
                    byte[] body = Encoding.UTF8.GetBytes(response);
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Mock\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                    await stream.WriteAsync(headers, 0, headers.Length); await stream.WriteAsync(body, 0, body.Length);
                }
            });
            try
            {
                using (var client = new ApiClient())
                    return client.TranslateAsync(new Settings { Endpoint = "http://127.0.0.1:" + port + "/v1", Model = "text-model", VisionModel = "vision-model", ApiKey = "mock-test-key" }, text, image, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally { server.GetAwaiter().GetResult(); listener.Stop(); }
        }
    }
}
