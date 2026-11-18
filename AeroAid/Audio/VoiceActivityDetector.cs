using System;
using System.Collections.Generic;

namespace MissionPlanner.AeroAid.Audio
{
    /// <summary>
    /// Speech front end for 16 kHz 16-bit mono PCM: a 2nd-order 90 Hz high-pass filter (removes DC, desk thumps and
    /// fan/motor rumble), an energy voice-activity detector with an adaptive noise floor, and level/clipping metering.
    ///
    /// The detector cuts the continuous microphone signal into UTTERANCES: speech starts when the 20 ms frame energy
    /// stays <see cref="StartMarginDb"/> above the noise floor for 60 ms; it ends after <see cref="HangoverMs"/> of
    /// quieter audio. Each utterance carries <see cref="PreRollMs"/> of audio from before the start so the first
    /// consonant ("d" of "disarm", "t" of "take off") is not cut off. Not thread-safe: feed it from one thread.
    /// </summary>
    public sealed class VoiceActivityDetector
    {
        public const int SampleRate = 16000;
        public const int FrameSamples = 320; // 20 ms
        public const int FrameMs = 20;

        /// <summary>Speech must be this far above the noise floor to start an utterance.</summary>
        public double StartMarginDb { get; set; } = 9;

        /// <summary>...and this far above it to keep an utterance going.</summary>
        public double ContinueMarginDb { get; set; } = 5;

        /// <summary>Absolute minimum level for speech (very quiet rooms).</summary>
        public double MinSpeechDb { get; set; } = -58;

        /// <summary>Silence that ends an utterance.</summary>
        public int HangoverMs { get; set; } = 650;

        /// <summary>Audio kept from before the detected start.</summary>
        public int PreRollMs { get; set; } = 300;

        /// <summary>Utterances shorter than this (clicks, taps) are discarded.</summary>
        public int MinSpeechMs { get; set; } = 120;

        /// <summary>An utterance is closed after this long even if the speech continues (background talk).</summary>
        public int MaxUtteranceMs { get; set; } = 6000;

        public bool InSpeech { get; private set; }

        /// <summary>Energy of the last frame, dBFS.</summary>
        public double FrameDb { get; private set; } = -100;

        /// <summary>Estimated background noise level, dBFS.</summary>
        public double NoiseFloorDb { get; private set; } = -60;

        /// <summary>Mean level of the last utterance's speech frames, dBFS (-100 before the first utterance).</summary>
        public double LastSpeechDb { get; private set; } = -100;

        /// <summary>Peak absolute sample of the last utterance, dBFS.</summary>
        public double LastPeakDb { get; private set; } = -100;

        /// <summary>Fraction of clipped samples in the last utterance.</summary>
        public double LastClipRatio { get; private set; }

        /// <summary>Utterance started; argument = pre-roll audio (filtered PCM).</summary>
        public event Action<byte[]> UtteranceStarted;

        /// <summary>More audio of the current utterance (filtered PCM).</summary>
        public event Action<byte[]> UtteranceAudio;

        /// <summary>Utterance ended; argument = true if long enough to be speech (false = discard).</summary>
        public event Action<bool> UtteranceEnded;

        // high-pass biquad state (Butterworth, fc = 90 Hz, fs = 16 kHz)
        private const double B0 = 0.97530991, B1 = -1.95061982, B2 = 0.97530991, A1 = -1.94999617, A2 = 0.95124347;
        private double x1, x2, y1, y2;

        private readonly short[] frame = new short[FrameSamples];
        private int frameFill;
        private readonly Queue<(byte[] pcm, double db)> preRoll = new Queue<(byte[], double)>();
        private readonly List<double> utteranceDb = new List<double>();

        /// <summary>
        /// Energy (dBFS) of every 20 ms frame of the current / last utterance, starting with the pre-roll - i.e.
        /// aligned with the audio handed to the recogniser (frame i covers i*20 ms .. (i+1)*20 ms).
        /// </summary>
        public IReadOnlyList<double> UtteranceFrameDb => utteranceDb;
        private int aboveCount, belowMs, speechMs, uttMs;
        private double speechEnergySum;
        private int speechFrames, clipped, samples, peak;
        private bool floorInitialised;

        /// <summary>Feeds raw microphone PCM (any length).</summary>
        public void Process(byte[] pcm, int count)
        {
            for (var i = 0; i + 1 < count; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                frame[frameFill++] = HighPass(s);
                if (frameFill == FrameSamples)
                {
                    OnFrame();
                    frameFill = 0;
                }
            }
        }

        public void Process(byte[] pcm) => Process(pcm, pcm.Length);

        private short HighPass(short s)
        {
            double x = s;
            var y = B0 * x + B1 * x1 + B2 * x2 - A1 * y1 - A2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            if (y > short.MaxValue) y = short.MaxValue;
            if (y < short.MinValue) y = short.MinValue;
            return (short)y;
        }

        /// <summary>Clears the utterance state (not the noise floor).</summary>
        public void Reset()
        {
            if (InSpeech)
                End(false);
            preRoll.Clear();
            aboveCount = 0;
        }

        private void OnFrame()
        {
            double sum = 0;
            var framePeak = 0;
            var frameClipped = 0;
            for (var i = 0; i < FrameSamples; i++)
            {
                double v = frame[i];
                sum += v * v;
                var a = Math.Abs((int)frame[i]);
                if (a > framePeak) framePeak = a;
                if (a >= 32000) frameClipped++;
            }

            var db = 10 * Math.Log10(sum / FrameSamples / (32768.0 * 32768.0) + 1e-12);
            FrameDb = db;
            var bytes = new byte[FrameSamples * 2];
            Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);

            if (!floorInitialised)
            {
                NoiseFloorDb = Math.Max(db, -90);
                floorInitialised = true;
            }

            if (!InSpeech)
            {
                // noise floor: follows quieter audio quickly, louder audio slowly (~4 s per 10 dB)
                if (db < NoiseFloorDb) NoiseFloorDb += (db - NoiseFloorDb) * 0.2;
                else NoiseFloorDb += Math.Min(db - NoiseFloorDb, 10) * 0.005;
                NoiseFloorDb = Math.Max(NoiseFloorDb, -90);

                preRoll.Enqueue((bytes, db));
                while (preRoll.Count > PreRollMs / FrameMs) preRoll.Dequeue();

                if (db > NoiseFloorDb + StartMarginDb && db > MinSpeechDb)
                    aboveCount++;
                else
                    aboveCount = 0;

                if (aboveCount >= 3)
                {
                    InSpeech = true;
                    belowMs = 0;
                    speechMs = aboveCount * FrameMs;
                    uttMs = 0;
                    speechEnergySum = 0;
                    speechFrames = 0;
                    clipped = 0;
                    samples = 0;
                    peak = 0;
                    var pre = new List<byte>();
                    utteranceDb.Clear();
                    foreach (var b in preRoll)
                    {
                        pre.AddRange(b.pcm);
                        utteranceDb.Add(b.db);
                    }

                    preRoll.Clear();
                    uttMs = pre.Count / 32;
                    UtteranceStarted?.Invoke(pre.ToArray());
                }

                return;
            }

            // in speech
            uttMs += FrameMs;
            samples += FrameSamples;
            utteranceDb.Add(db);
            clipped += frameClipped;
            if (framePeak > peak) peak = framePeak;
            UtteranceAudio?.Invoke(bytes);
            if (db > NoiseFloorDb + ContinueMarginDb && db > MinSpeechDb - 4)
            {
                belowMs = 0;
                speechMs += FrameMs;
                speechEnergySum += Math.Pow(10, db / 10);
                speechFrames++;
            }
            else
            {
                belowMs += FrameMs;
            }

            if (belowMs >= HangoverMs || uttMs >= MaxUtteranceMs)
                End(speechMs >= MinSpeechMs);
        }

        private void End(bool valid)
        {
            InSpeech = false;
            aboveCount = 0;
            if (speechFrames > 0)
                LastSpeechDb = 10 * Math.Log10(speechEnergySum / speechFrames + 1e-12);
            LastPeakDb = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -100;
            LastClipRatio = samples > 0 ? (double)clipped / samples : 0;
            UtteranceEnded?.Invoke(valid);
        }
    }

    /// <summary>Level statistics of a recorded microphone test.</summary>
    public sealed class MicTestResult
    {
        public double PeakDb, SpeechDb, NoiseDb, ClipPercent;
        public bool AnySignal;
        public byte[] Wav;

        public double SnrDb => SpeechDb - NoiseDb;

        /// <summary>(verdict, ok) for the operator.</summary>
        public (string text, bool ok) Verdict()
        {
            if (!AnySignal)
                return ("NO SIGNAL - the microphone delivers silence. Check Windows Settings › Privacy & security › Microphone " +
                        "(allow desktop apps), that the device is not muted, and that the right device is selected.", false);
            if (ClipPercent > 0.1 || PeakDb > -0.5)
                return ($"CLIPPING - the input is too loud ({ClipPercent:0.00} % clipped samples). Lower the microphone level " +
                        "in Windows Sound settings or move the microphone further away.", false);
            if (SpeechDb < -45)
                return ($"TOO QUIET - speech at {SpeechDb:0} dBFS. Raise the microphone level in Windows Sound settings " +
                        "(aim for -30 to -12 dBFS) or speak closer to the microphone.", false);
            if (SnrDb < 12)
                return ($"NOISY - speech is only {SnrDb:0} dB above the background ({NoiseDb:0} dBFS). Reduce background " +
                        "noise or use a headset microphone.", false);
            return ($"GOOD - speech {SpeechDb:0} dBFS, peak {PeakDb:0} dBFS, background {NoiseDb:0} dBFS (SNR {SnrDb:0} dB).", true);
        }

        /// <summary>Computes the statistics of 16 kHz 16-bit mono PCM.</summary>
        public static MicTestResult Analyse(byte[] pcm)
        {
            var r = new MicTestResult();
            var n = pcm.Length / 2;
            var frames = new List<double>();
            var peak = 0;
            var clipped = 0;
            double sum = 0;
            var count = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var s = (short)(pcm[i] | (pcm[i + 1] << 8));
                var a = Math.Abs((int)s);
                if (a > 0) r.AnySignal = true;
                if (a > peak) peak = a;
                if (a >= 32000) clipped++;
                sum += (double)s * s;
                if (++count == VoiceActivityDetector.FrameSamples)
                {
                    frames.Add(10 * Math.Log10(sum / count / (32768.0 * 32768.0) + 1e-12));
                    sum = 0;
                    count = 0;
                }
            }

            frames.Sort();
            r.PeakDb = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -100;
            r.ClipPercent = n > 0 ? 100.0 * clipped / n : 0;
            if (frames.Count > 0)
            {
                // background = quietest 20 % of frames, speech = loudest 20 %
                var k = Math.Max(1, frames.Count / 5);
                r.NoiseDb = Mean(frames.GetRange(0, k));
                r.SpeechDb = Mean(frames.GetRange(frames.Count - k, k));
            }
            else
            {
                r.NoiseDb = r.SpeechDb = -100;
            }

            r.Wav = ToWav(pcm);
            return r;
        }

        private static double Mean(List<double> db)
        {
            double e = 0;
            foreach (var d in db) e += Math.Pow(10, d / 10);
            return 10 * Math.Log10(e / db.Count + 1e-12);
        }

        /// <summary>Wraps 16 kHz 16-bit mono PCM in a WAV header.</summary>
        public static byte[] ToWav(byte[] pcm)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + pcm.Length);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(VoiceActivityDetector.SampleRate);
                w.Write(VoiceActivityDetector.SampleRate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(pcm.Length);
                w.Write(pcm);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
