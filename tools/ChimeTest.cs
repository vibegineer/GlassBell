// Test harness for Chime.cs: writes each chime to a wav file and prints loudness and spectrum figures.
// It never plays anything. Build:  csc /target:exe /out:chimetest.exe Chime.cs ChimeTest.cs
using System;
using System.IO;

namespace GlassBell
{
    static class Log { public static void Write(string s) { Console.WriteLine("log: " + s); } }

    static class ChimeTest
    {
        static int Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            if (args.Length > 1)
            {
                // preview mode: just write the files at the given gain, named after the style
                // args[1] = volume setting 0..100: written at the level the program would use with the system at 0 dB
                int vol = int.Parse(args[1]);
                for (int s = 0; s < 3; s++)
                {
                    Chime.BuildWav(s, 1.0);   // makes sure the average level is known
                    File.WriteAllBytes(Path.Combine(dir, (s + 1) + " - " + Chime.Names[s] + ".wav"), Chime.BuildWav(s, Chime.Gain(vol, 0, Chime.RmsDb[s])));
                }
                return 0;
            }
            for (int style = 0; style < 3; style++)
            {
                byte[] wav = Chime.BuildWav(style, 1.0);
                File.WriteAllBytes(Path.Combine(dir, "chime" + style + ".wav"), wav);
                int n = (wav.Length - 44) / 4;
                var x = new double[n];
                double peak = 0, sum = 0;
                for (int i = 0; i < n; i++)
                {
                    double l = BitConverter.ToInt16(wav, 44 + i * 4) / 32768.0;
                    double r = BitConverter.ToInt16(wav, 44 + i * 4 + 2) / 32768.0;
                    x[i] = 0.5 * (l + r);
                    peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                    sum += 0.5 * (l * l + r * r);
                }
                double rms = Math.Sqrt(sum / n);

                // spectrum of the first 1.2 s with a Hann window (naive DFT at 20 Hz steps is fast enough here)
                int m = (int)(1.2 * Chime.Rate);
                double[] band = new double[5]; double low = 0, bandTotal = 0, aTotal = 0, flatTotal = 0, centroidNum = 0, centroidDen = 0;
                for (double f = 100; f <= 12000; f += 20)
                {
                    double re = 0, im = 0;
                    for (int i = 0; i < m; i++)
                    {
                        double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / m);
                        double ph = 2 * Math.PI * f * i / Chime.Rate;
                        re += x[i] * win * Math.Cos(ph);
                        im -= x[i] * win * Math.Sin(ph);
                    }
                    double p = re * re + im * im;
                    double a = AWeight(f);
                    flatTotal += p; aTotal += p * a * a;
                    centroidNum += p * f; centroidDen += p;
                    int b = f < 500 ? 0 : f < 1000 ? 1 : f < 4000 ? 2 : f < 8000 ? 3 : 4;
                    if (f < 700) low += p;
                    band[b] += p; bandTotal += p;
                }
                Console.WriteLine("style {0} ({1}): {2:F2} s  peak {3:F1} dBFS  rms {4:F1} dBFS  crest {5:F1} dB  (mix before normalising peaked at {6:F2})",
                    style, Chime.Names[style], n / (double)Chime.Rate, 20 * Math.Log10(peak), 20 * Math.Log10(rms), 20 * Math.Log10(peak / rms), Chime.PrePeak[style]);
                Console.WriteLine("   energy: <500 Hz {0:P0}  500-1k {1:P0}  1-4k {2:P0}  4-8k {3:P0}  >8k {4:P0}   (<700 Hz {7:P0})  centroid {5:F0} Hz   A-weighted vs flat {6:F1} dB",
                    band[0] / bandTotal, band[1] / bandTotal, band[2] / bandTotal, band[3] / bandTotal, band[4] / bandTotal,
                    centroidNum / centroidDen, 10 * Math.Log10(aTotal / flatTotal), low / bandTotal);
            }
            for (int s = 0; s < 3; s++)
                Console.WriteLine("{0}: gain at vol 65 -> master 0 dB {1:F3} (net peak {5:F1} dBFS, average {6:F1} dBFS), -10 dB {2:F3}, -20 dB {3:F3}, -40 dB {4:F3}",
                    Chime.Names[s], Chime.Gain(65, 0, Chime.RmsDb[s]), Chime.Gain(65, -10, Chime.RmsDb[s]), Chime.Gain(65, -20, Chime.RmsDb[s]), Chime.Gain(65, -40, Chime.RmsDb[s]),
                    -0.7 + 20 * Math.Log10(Chime.Gain(65, 0, Chime.RmsDb[s])), Chime.RmsDb[s] + 20 * Math.Log10(Chime.Gain(65, 0, Chime.RmsDb[s])));
            float db; bool muted;
            SystemVolume.Read(out db, out muted);
            Console.WriteLine("this PC: master {0:F1} dB, muted {1}", db, muted);
            return 0;
        }

        // IEC A-weighting, linear amplitude
        static double AWeight(double f)
        {
            double f2 = f * f;
            double ra = (12194.0 * 12194.0 * f2 * f2) /
                ((f2 + 20.6 * 20.6) * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194.0));
            return ra * 1.2589;   // +2.0 dB so that 1 kHz is 0 dB
        }
    }
}
