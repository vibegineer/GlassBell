// L: the interface language, shared by GlassBell.exe and GlassBell-Setup.exe.
// The strings live in lang\<code>.txt ("key = value", UTF-8, "\n" for a line break), embedded as lang.<code>.txt.
// The language is the Windows display language unless settings.ini says lang=<code>; a language GlassBell has no
// file for is shown in English, and so is any key a translation lacks.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBell
{
    static class L
    {
        // the languages GlassBell ships with, by number of speakers
        public static readonly string[] Codes =
            { "en", "zh", "hi", "es", "ar", "fr", "bn", "pt", "ru", "id", "ur", "de", "ja", "tr", "vi", "ko", "it", "fa", "pl", "uk" };

        static readonly object gate = new object();
        static Dictionary<string, string> strings, english;
        static string code = "en";

        public static string Code { get { Ready(); return code; } }

        // Arabic, Urdu and Persian are written right to left
        public static bool Rtl { get { string c = Code; return c == "ar" || c == "ur" || c == "fa"; } }

        // the language WPF is told, so that the fallback fonts for Chinese, Japanese and Korean get the right glyphs
        public static string Ietf { get { string c = Code; return c == "zh" ? "zh-Hans" : c; } }

        // wanted: a language code, or "auto" / null for the Windows display language
        public static void Init(string wanted)
        {
            lock (gate)
            {
                code = Pick(wanted);
                english = Read("en") ?? new Dictionary<string, string>();
                strings = code == "en" ? english : (Read(code) ?? english);
            }
        }

        static void Ready() { if (strings == null) Init(null); }

        [DllImport("kernel32.dll")]
        static extern ushort GetUserDefaultUILanguage();

        static string Pick(string wanted)
        {
            string w = (wanted ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(Codes, w) >= 0) return w;
            // the display language chosen in Windows settings; CurrentUICulture can be one a parent program set for itself
            string ui;
            try { ui = new CultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName; }   // zh-CN and zh-TW are both "zh", pt-BR "pt"
            catch { ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName; }
            return Array.IndexOf(Codes, ui) >= 0 ? ui : "en";
        }

        static Dictionary<string, string> Read(string lang)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("lang." + lang + ".txt"))
                {
                    if (s == null) return null;
                    var d = new Dictionary<string, string>();
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        for (string line; (line = r.ReadLine()) != null; )
                        {
                            int eq = line.IndexOf('=');
                            if (line.TrimStart().StartsWith("#") || eq <= 0) continue;
                            d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                        }
                    return d;
                }
            }
            catch { return null; }
        }

        public static string T(string key)
        {
            Ready();
            string s;
            if (strings.TryGetValue(key, out s) || english.TryGetValue(key, out s)) return s;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException) { return T(key); }
        }
    }
}
