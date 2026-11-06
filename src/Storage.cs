using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;

namespace EasyIP
{
    public static class Log
    {
        const long MaxBytes = 512 * 1024;
        static readonly object Gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(Storage.Folder, "easyip.log"); }
        }

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Storage.Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Copy(FilePath, FilePath + ".old", true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { /* logging must never break the app */ }
        }
    }

    public static class Storage
    {
        /// <summary>Overrides the folder in tests.</summary>
        public static string FolderOverride;

        public static string Folder
        {
            get
            {
                return FolderOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyIP");
            }
        }

        static string FilePath { get { return Path.Combine(Folder, "settings.xml"); } }

        public static Settings Load()
        {
            var empty = new Settings();
            empty.Normalize();
            try
            {
                if (!File.Exists(FilePath)) return empty;
                using (var stream = File.OpenRead(FilePath))
                {
                    var s = (Settings)new DataContractSerializer(typeof(Settings)).ReadObject(stream);
                    s.Normalize();
                    return s;
                }
            }
            catch (Exception ex)
            {
                // A damaged file must not stop the app. Keep a copy and start clean.
                Log.Write("settings unreadable: " + ex.Message);
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                return empty;
            }
        }

        public static bool Save(Settings s)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true }))
                    new DataContractSerializer(typeof(Settings)).WriteObject(writer, s);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("settings not saved: " + ex.Message);
                return false;
            }
        }
    }
}
