using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;

namespace LunaKit
{
    /// LunaKit's log, with every page's console and palm log in it: the debugger's output, and
    /// a file in the app's LocalState (lunakit.log, the last run's in lunakit.1.log) that Device
    /// Portal's file explorer can pull off a phone. Secrets are blanked first, as AndroidLuna's
    /// Bus.redact blanks them: tokens and passwords in JSON and query strings, and email
    /// addresses.
    internal static class LunaLog
    {
        private static readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        private static readonly object startLock = new object();
        private static bool started;

        private static readonly Regex secretJson = new Regex(
            "(\"(?:[A-Za-z]*password|passwd|pin|secret|[A-Za-z]*[Tt]oken|client_secret|username)\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",
            RegexOptions.IgnoreCase);
        private static readonly Regex secretQuery = new Regex(
            "([?&](?:auth_token|token|password|[A-Za-z]*Token)=)[^&\\s\"]*", RegexOptions.IgnoreCase);
        private static readonly Regex email = new Regex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}");

        public static void Write(string text)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + Redact(text);
            Debug.WriteLine(line);
            Start();
            queue.Add(line);
        }

        internal static string Redact(string s)
        {
            s = secretJson.Replace(s, "$1\"…\"");
            s = secretQuery.Replace(s, "$1…");
            return email.Replace(s, "…@…");
        }

        private static void Start()
        {
            if (started) return;
            lock (startLock)
            {
                if (started) return;
                started = true;
                var dir = ApplicationData.Current.LocalFolder.Path;
                Task.Factory.StartNew(() => Pump(dir), TaskCreationOptions.LongRunning);
            }
        }

        private static void Pump(string dir)
        {
            var path = Path.Combine(dir, "lunakit.log");
            try
            {
                var old = Path.Combine(dir, "lunakit.1.log");
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(path)) File.Move(path, old);
            }
            catch (Exception) { }
            foreach (var line in queue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(path, line + "\r\n"); } catch (Exception) { }
            }
        }
    }
}
