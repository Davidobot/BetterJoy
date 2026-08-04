using System.Drawing;
using System.Resources;
using System.Threading;

namespace BetterJoyForCemu {
    public static class I18n {
        private static readonly ResourceManager rm =
            new ResourceManager("BetterJoyForCemu.Strings", typeof(I18n).Assembly);

        public static string Str(string key) {
            var s = rm.GetString(key, Thread.CurrentThread.CurrentUICulture) ?? key;
            return s.Replace("\\r\\n", "\r\n").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        public static string Format(string key, params object[] args) {
            return string.Format(Str(key), args);
        }

        public static Font UiFont {
            get {
                if (Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh"))
                    return new Font("Microsoft YaHei UI", 8.25F);
                return null;
            }
        }
    }
}
