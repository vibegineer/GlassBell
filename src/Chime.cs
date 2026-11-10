// Chime: the GlassBell notification sound, synthesised in code (no audio files, no licensing).
//
// Three original two/three-note chimes in a glassy, bell-like timbre with a soft attack and a smooth tail:
//   0 "Crystal"  two glass-bell notes a fifth apart, a faint octave shimmer on the second
//   1 "Silk"     a warm three-note rising arpeggio, longer and softer
//   2 "Drop"     a single note that blooms down into pitch, with a quiet answer above it
// (the names the settings window shows come from the language files: chime.0, chime.1, chime.2)
// All of them keep their energy between 1 and 4 kHz (where hearing is most sensitive) and start with a tiny
// "glass tap" transient, so they stay audible at low system volume without having to be loud.
//
// Loudness follows the system volume: the program reads the master volume of the default output device and
// raises the chime when the system is quiet and lowers it when the system is loud, so the chime always lands
// at about the same level at the speakers (capped at full scale; below that the system volume wins).
using System;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;

namespace GlassBell
{
    static class Chime
    {
        public const int Rate = 44100;
        public static readonly string[] Names = { "Crystal", "Silk", "Drop" };

        public static readonly double[] PrePeak = new double[3];   // peak of the raw mix, for the test harness
        public static readonly double[] RmsDb = new double[3];     // average level of the finished chime (dBFS)
        static readonly float[][] left = new float[3][];
        static readonly float[][] right = new float[3][];
        static readonly object gate = new object();
        static DateTime last = DateTime.MinValue;
        static SoundPlayer player;

        // ---- playing ------------------------------------------------------------------

        // vol 0..100 is the wanted level, adapt switches the follow-the-system-volume behaviour on
        public static void Play(int style, int vol, bool adapt)
        {
            try
            {
                lock (gate)
                {
                    if ((DateTime.UtcNow - last).TotalMilliseconds < 350) return;   // a burst of toasts is one chime
                    last = DateTime.UtcNow;
                }
                style = Math.Max(0, Math.Min(2, style));
                Ensure(style);
                float masterDb; bool muted;
                SystemVolume.Read(out masterDb, out muted);
                if (muted) return;
                double gain = Gain(vol, adapt ? masterDb : 0, RmsDb[style]);
                byte[] wav = BuildWav(style, gain);
                lock (gate)
                {
                    if (player != null) { try { player.Stop(); player.Dispose(); } catch { } }
                    player = new SoundPlayer(new MemoryStream(wav));
                    player.Play();
                }
            }
            catch (Exception e) { Log.Write("chime: " + e.Message); }
        }

        // Average level wanted at the speakers: -36 dB at vol 0 up to -12 dB at vol 100 (relative to full scale at
        // 0 dB master volume). A quiet system gets the difference added, up to digital full scale. Judged by the
        // average level of the chime, not its peak, so the three chimes feel equally loud.
        public static double Gain(int vol, double masterDb, double chimeRmsDb)
        {
            double targetDb = -36 + 0.24 * Math.Max(0, Math.Min(100, vol));
            double gDb = Math.Min(0, targetDb - chimeRmsDb - masterDb);
            return Math.Pow(10, gDb / 20);
        }

        static void Ensure(int style)
        {
            lock (gate)
            {
                if (left[style] != null) return;
                float[] l, r;
                Synth(style, out l, out r);
                left[style] = l; right[style] = r;
            }
        }

        // ---- wav ----------------------------------------------------------------------

        public static byte[] BuildWav(int style, double gain)
        {
            style = Math.Max(0, Math.Min(2, style));
            Ensure(style);
            float[] L = left[style], R = right[style];
            int n = L.Length;
            var ms = new MemoryStream(44 + n * 4);
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 4); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Max(-32767, Math.Min(32767, Math.Round(L[i] * gain * 32767))));
                w.Write((short)Math.Max(-32767, Math.Min(32767, Math.Round(R[i] * gain * 32767))));
            }
            w.Flush();
            return ms.ToArray();
        }

        // ---- synthesis ----------------------------------------------------------------

        // a bell-like voice: a few partials at the given ratios, each decaying faster than the one below,
        // a raised-cosine attack and an optional pitch "bloom" that settles in a few tens of milliseconds
        // accent > 0 adds a short punch to the start of the note (the envelope overshoots and settles in ~20 ms)
        static void Voice(float[] b, double start, double f, double amp, double decay, double attack,
            double[] ratios, double[] amps, double bloom, double accent)
        {
            int s0 = (int)(start * Rate);
            int n = (int)(decay * 6.0 * Rate);
            var phase = new double[ratios.Length];
            for (int i = 0; i < n && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double att = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack) : 1.0;
                double glide = 1 + bloom * Math.Exp(-t / 0.035);
                double punch = 1 + accent * Math.Exp(-t / 0.022);
                double sum = 0;
                for (int p = 0; p < ratios.Length; p++)
                {
                    phase[p] += 2 * Math.PI * f * ratios[p] * glide / Rate;
                    sum += amps[p] * Math.Exp(-t / (decay / (1 + 0.9 * p))) * Math.Sin(phase[p]);
                }
                b[s0 + i] += (float)(amp * att * punch * sum);
            }
        }

        // a soft low "tok" under the start of a note: a short sine that settles from ~190 to 150 Hz, which gives the
        // chime some body without being a kick drum. Kept well above the sub-bass, small speakers can play it.
        static void Thump(float[] b, double start, double amp)
        {
            int s0 = (int)(start * Rate);
            double ph = 0;
            for (int i = 0; i < (int)(0.15 * Rate) && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                ph += 2 * Math.PI * (150 + 45 * Math.Exp(-t / 0.02)) / Rate;
                b[s0 + i] += (float)(amp * Math.Min(1.0, t / 0.004) * Math.Exp(-t / 0.045) * Math.Sin(ph));
            }
        }

        // a very short pure-tone tick, the "glass tap" that gives the chime a clear start at low volume.
        // It is a sine, not noise: noise bursts are what made the first version sound hoarse.
        static void Tap(float[] b, double start, double amp)
        {
            int s0 = (int)(start * Rate);
            for (int i = 0; i < (int)(0.012 * Rate) && s0 + i < b.Length; i++)
            {
                double t = i / (double)Rate;
                double rise = Math.Min(1.0, t / 0.0004);     // 0.4 ms rise, so there is no click
                b[s0 + i] += (float)(amp * rise * Math.Exp(-t / 0.0018) * Math.Sin(2 * Math.PI * 4186.0 * t));
            }
        }

        static void Synth(int style, out float[] outL, out float[] outR)
        {
            double secs = style == 1 ? 2.0 : 1.7;
            int n = (int)(secs * Rate);
            var dry = new float[n];

            // The low layer repeats the notes two octaves down in a soft, warm tone, so the chime has some body
            // under the glassy top. Its level is about half of the top notes: noticeable, not boomy.
            double[] lowR = { 1.0, 2.0, 3.0 }, lowA = { 1.0, 0.22, 0.06 };

            if (style == 0)
            {
                double[] rt = { 1.0, 2.756, 5.404, 8.933 }, am = { 1.0, 0.20, 0.08, 0.03 };
                Voice(dry, 0.000, 987.77, 0.75, 0.42, 0.006, rt, am, 0, 0.35);    // B5
                Voice(dry, 0.125, 1479.98, 1.00, 0.80, 0.006, rt, am, 0, 0.35);   // F#6
                Voice(dry, 0.125, 1975.53, 0.22, 0.55, 0.006, rt, am, 0, 0);      // B6, the shimmer
                Voice(dry, 0.000, 246.94, 0.50, 0.50, 0.014, lowR, lowA, 0, 0);   // B3
                Voice(dry, 0.125, 369.99, 0.55, 0.75, 0.014, lowR, lowA, 0, 0);   // F#4
                Thump(dry, 0.000, 0.20);
                Tap(dry, 0.000, 0.12); Tap(dry, 0.125, 0.10);
            }
            else if (style == 1)
            {
                // exact harmonics (a detuned partial beats against the next note), and the upper ones kept quiet
                double[] rt = { 1.0, 2.0, 3.0 }, am = { 1.0, 0.07, 0.02 };
                Voice(dry, 0.000, 1174.66, 0.80, 0.45, 0.012, rt, am, 0, 0.30);   // D6
                Voice(dry, 0.085, 1479.98, 0.85, 0.50, 0.012, rt, am, 0, 0.30);   // F#6
                Voice(dry, 0.170, 1760.00, 0.90, 0.95, 0.012, rt, am, 0, 0.35);   // A6
                Voice(dry, 0.170, 2349.32, 0.12, 0.80, 0.012, rt, am, 0, 0);      // D7
                Voice(dry, 0.000, 293.66, 0.50, 0.55, 0.016, lowR, lowA, 0, 0);   // D4
                Voice(dry, 0.170, 440.00, 0.60, 1.00, 0.016, lowR, lowA, 0, 0);   // A4
                Thump(dry, 0.000, 0.18);
                Tap(dry, 0.000, 0.07); Tap(dry, 0.085, 0.06); Tap(dry, 0.170, 0.09);
            }
            else
            {
                double[] rt = { 1.0, 2.0, 4.0 }, am = { 1.0, 0.35, 0.08 };
                Voice(dry, 0.000, 1046.50, 1.00, 0.55, 0.003, rt, am, 0.06, 0.35); // C6, blooms down into pitch
                Voice(dry, 0.160, 1567.98, 0.50, 0.70, 0.006, rt, am, 0, 0.25);    // G6
                Voice(dry, 0.000, 261.63, 0.60, 0.60, 0.012, lowR, lowA, 0, 0);    // C4
                Voice(dry, 0.160, 392.00, 0.40, 0.70, 0.014, lowR, lowA, 0, 0);    // G4
                Thump(dry, 0.000, 0.22);
                Tap(dry, 0.000, 0.14); Tap(dry, 0.160, 0.06);
            }

            // a soft room: four combs and two allpasses (Schroeder), the right channel a little different
            var wetL = Reverb(dry, new[] { 1116, 1188, 1277, 1356 }, new[] { 556, 441 });
            var wetR = Reverb(dry, new[] { 1139, 1211, 1300, 1379 }, new[] { 579, 464 });
            int haas = (int)(0.006 * Rate);                                  // 6 ms: a touch of width
            var l = new float[n]; var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                l[i] = dry[i] + 0.20f * wetL[i];
                r[i] = (i >= haas ? 0.92f * dry[i - haas] : 0f) + 0.20f * wetR[i];
            }

            // Level the chime by its average loudness, then tame only the short peaks with a smooth look-ahead
            // limiter. (The first version ran everything through tanh; overlapping notes pushed the mix to twice
            // full scale, so that was heavy distortion, the hoarseness.)
            double peak = 0, sq = 0;
            for (int i = 0; i < n; i++)
            {
                peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                sq += 0.5 * ((double)l[i] * l[i] + (double)r[i] * r[i]);
            }
            PrePeak[style] = peak;
            double rms0 = Math.Sqrt(sq / n);
            // a lower average level than before (0.20): the sharper starts of the notes pass the limiter untouched and
            // the output gain, which follows the average level, brings the chime back to the same loudness
            double lvl = rms0 > 0 ? 0.20 / rms0 : 1;
            for (int i = 0; i < n; i++) { l[i] = (float)(l[i] * lvl); r[i] = (float)(r[i] * lvl); }
            Limit(l, r, 0.92);

            int fadeIn = (int)(0.001 * Rate), fadeOut = (int)(0.25 * Rate);
            double sq2 = 0;
            for (int i = 0; i < n; i++)
            {
                double e = 1;
                if (i < fadeIn) e = i / (double)fadeIn;
                if (i > n - fadeOut) e = Math.Min(e, 0.5 + 0.5 * Math.Cos(Math.PI * (i - (n - fadeOut)) / fadeOut));
                l[i] = (float)(l[i] * e);
                r[i] = (float)(r[i] * e);
                sq2 += 0.5 * ((double)l[i] * l[i] + (double)r[i] * r[i]);
            }
            RmsDb[style] = 10 * Math.Log10(sq2 / n);
            outL = l; outR = r;
        }

        // Look-ahead limiter: the gain dips smoothly (a few milliseconds before and after a peak) instead of
        // clipping it, so the tone stays clean.
        static void Limit(float[] l, float[] r, double ceiling)
        {
            int n = l.Length, w = (int)(0.003 * Rate);
            var need = new float[n];
            for (int i = 0; i < n; i++) need[i] = 1f;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                double p = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
                if (p <= ceiling) continue;
                any = true;
                float g = (float)(ceiling / p);
                for (int j = Math.Max(0, i - w); j <= Math.Min(n - 1, i + w); j++) if (g < need[j]) need[j] = g;
            }
            if (!any) return;
            // moving average over the same width, so the gain curve has no corners
            var sm = new float[n];
            var pre = new double[n + 1];
            for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + need[i];
            int len = 2 * w + 1;
            for (int i = 0; i < n; i++)
            {
                int a = i - w, b = i + w;
                int ca = Math.Max(0, a), cb = Math.Min(n - 1, b);
                double sum = pre[cb + 1] - pre[ca] + ((ca - a) + (b - cb));   // positions outside the file count as gain 1
                sm[i] = (float)(sum / len);
            }
            for (int i = 0; i < n; i++) { l[i] *= sm[i]; r[i] *= sm[i]; }
        }

        static float[] Reverb(float[] x, int[] combDelays, int[] allpassDelays)
        {
            int n = x.Length;
            var y = new float[n];
            foreach (int d in combDelays)
            {
                var buf = new float[d];
                int pos = 0;
                float lp = 0;
                for (int i = 0; i < n; i++)
                {
                    float o = buf[pos];
                    lp = o * 0.45f + lp * 0.55f;                 // damping: the tail gets darker as it fades
                    buf[pos] = x[i] + lp * 0.72f;
                    pos = pos + 1 == d ? 0 : pos + 1;
                    y[i] += o * 0.25f;
                }
            }
            foreach (int d in allpassDelays)
            {
                var buf = new float[d];
                int pos = 0;
                for (int i = 0; i < n; i++)
                {
                    float o = buf[pos];
                    float v = y[i] + o * 0.5f;
                    buf[pos] = v;
                    y[i] = o - v * 0.5f;
                    pos = pos + 1 == d ? 0 : pos + 1;
                }
            }
            // the room is soft: no highs above about 5 kHz in the tail
            float state = 0;
            for (int i = 0; i < n; i++) { state += 0.5f * (y[i] - state); y[i] = state; }
            return y;
        }
    }

    // master volume and mute state of the default output device (Core Audio)
    static class SystemVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // masterDb is <= 0 (0 = full volume); when the device cannot be read it stays 0 and nothing is muted
        public static void Read(out float masterDb, out bool muted)
        {
            masterDb = 0; muted = false;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out dev) != 0) return;
                Guid iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                object o;
                if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0) return;
                var vol = (IAudioEndpointVolume)o;
                float db;
                if (vol.GetMasterVolumeLevel(out db) == 0 && !float.IsInfinity(db) && !float.IsNaN(db)) masterDb = Math.Min(0, db);
                bool m;
                if (vol.GetMute(out m) == 0) muted = m;
            }
            catch (Exception e) { Log.Write("system volume: " + e.Message); }
        }
    }
}
