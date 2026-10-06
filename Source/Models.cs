using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace FloatTranslate
{
    public sealed class Settings
    {
        public string Endpoint { get; set; }
        public string EncryptedKey { get; set; }
        public string Model { get; set; }
        public string VisionModel { get; set; }
        public string Prompt { get; set; }
        public string SelectionShortcut { get; set; }
        public string CaptureShortcut { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public bool KeepHistory { get; set; }
        public bool StartWithWindows { get; set; }
        public bool GlassEffect { get; set; }
        public double GlassTransparency { get; set; }
        [ScriptIgnore] public string ApiKey { get; set; }

        public Settings()
        {
            Endpoint = "https://api.openai.com/v1"; ApiKey = ""; EncryptedKey = "";
            Model = ""; VisionModel = ""; Prompt = "";
            SelectionShortcut = "Ctrl + Alt + T"; CaptureShortcut = "Ctrl + Alt + S";
            SourceLanguage = "自动检测"; TargetLanguage = "简体中文"; KeepHistory = true; GlassEffect = true; GlassTransparency = 45;
        }
        public Settings Copy() { return (Settings)MemberwiseClone(); }
        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(Endpoint); } }
        public void Validate()
        {
            ApiClient.BuildEndpoint(Endpoint);
            if (string.IsNullOrWhiteSpace(Model)) throw new Exception("请填写文字模型 ID。");
            if ((ApiKey ?? "").IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new Exception("API Key 中不能包含换行，请重新粘贴密钥。");
            if (Model.Length > 200 || VisionModel.Length > 200) throw new Exception("模型 ID 太长，请检查输入。");
            Hotkey.Parse(SelectionShortcut); Hotkey.Parse(CaptureShortcut);
            if (Hotkey.Parse(SelectionShortcut).Equals(Hotkey.Parse(CaptureShortcut))) throw new Exception("两个快捷键不能相同。");
        }
    }
    public sealed class HistoryEntry
    {
        public string Source { get; set; }
        public string Translation { get; set; }
        public string Target { get; set; }
        public string Model { get; set; }
        public DateTime Time { get; set; }
    }
    public sealed class Store
    {
        public readonly string DirectoryPath;
        public string Warning = "";
        public Settings Current;
        public List<HistoryEntry> History = new List<HistoryEntry>();
        public Store(string directory)
        {
            DirectoryPath = directory;
            Current = Read<Settings>("settings.json") ?? new Settings();
            try { Current.ApiKey = Unprotect(Current.EncryptedKey); }
            catch { Current.ApiKey = ""; Warning = "密钥无法解密，请在设置中重新填写。"; }
            History = Read<List<HistoryEntry>>("history.json") ?? new List<HistoryEntry>();
            History.RemoveAll(delegate(HistoryEntry item) { return item == null || item.Translation == null; });
            if (History.Count > 100) History = History.GetRange(0, 100);
        }
        private T Read<T>(string name) where T : class
        {
            string path = Path.Combine(DirectoryPath, name);
            if (!File.Exists(path)) return null;
            try { return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch { Warning = "本地配置或记录无法读取，已使用默认值。原文件保留在数据目录。"; return null; }
        }
        private void Write(string name, object value)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, name);
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        public void SaveSettings(Settings settings)
        {
            Settings copy = settings.Copy(); copy.EncryptedKey = Protect(copy.ApiKey);
            Write("settings.json", copy); Current = copy;
        }
        public void Remember(HistoryEntry entry)
        {
            if (!Current.KeepHistory) return;
            History.Insert(0, entry);
            if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
            Write("history.json", History);
        }
        public void ClearHistory() { Write("history.json", new List<HistoryEntry>()); History.Clear(); }
        public static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        public static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        public static void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("FloatTranslate", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --background");
                else key.DeleteValue("FloatTranslate", false);
            }
        }
    }
}
