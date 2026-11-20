using System;
using System.Collections.Generic;

namespace MissionPlanner.AeroAid.Audio
{
    /// <summary>
    /// Speech front end for 16 kHz 16-bit mono PCM: a 2nd-order 90 Hz high-pass filter (removes DC, desk thumps and
    /// fan/motor rumble), an energy voice-activity detector with an adaptive noise model, and level/clipping metering.
    ///
    /// The detector cuts the continuous microphone signal into UTTERANCES: speech starts when the 20 ms frame energy
    /// stays above the start threshold for 60 ms; it ends when the last <see cref="HangoverMs"/> were (almost all)
    /// quiet. Each utterance carries <see cref="PreRollMs"/> of audio from before the start so the first consonant
    /// ("d" of "disarm", "t" of "take off") is not cut off. Not thread-safe: feed it from one thread.
    ///
    /// Noise model: the median (<see cref="NoiseFloorDb"/>) and 90th percentile (<see cref="NoisePeakDb"/>) of the
    /// last 3 s of background frames. Thresholds sit above the background's OWN fluctuation, not just above its
    /// level. The previous detector tracked the minimum of the background and required every frame of the
    /// hang-over to be below floor+5 dB; with a real laptop microphone array (frame level fluctuating by 3 dB or
    /// more) that never happened, so every utterance ran to the 6 s limit, collected seconds of noise, and correct
    /// commands were rejected as "INCOMPLETE" (see VoiceRealNoiseTests).
    /// </summary>
    public sealed class VoiceActivityDetector
    {
        public const int SampleRate = 16000;
        public const int FrameSamples = 320; // 20 ms
        public const int FrameMs = 20;

        /// <summary>Speech must be this far above the noise median to start an utterance...</summary>
        public double StartMarginDb { get; set; } = 9;

        /// <summary>...and this far above the background's 90th percentile.</summary>
        public double StartAbovePeakDb { get; set; } = 4;

        /// <summary>A frame counts as speech (keeps an utterance going) this far above the noise median...</summary>
        public double ContinueMarginDb { get; set; } = 5;

        /// <summary>...and this far above the background's 90th percentile.</summary>
        public double ContinueAbovePeakDb { get; set; } = 1.5;

        /// <summary>
        /// An utterance ends when at most this fraction of the frames in the last <see cref="HangoverMs"/> were
        /// speech frames (isolated background peaks do not keep it open).
        /// </summary>
        public double EndSpeechFraction { get; set; } = 0.12;

        /// <summary>Absolute minimum level for speech (very quiet rooms).</summary>
        public double MinSpeechDb { get; set; } = -58;

        /// <summary>Silence that ends an utterance.</summary>
        public int HangoverMs { get; set; } = 650;

        /// <summary>Audio kept from before the detected start.</summary>
        public int PreRollMs { get; set; } = 300;

        /// <summary>Utterances shorter than this (clicks, taps) are discarded.</summary>
        public int MinSpeechMs { get; set; } = 120;

        /// <summary>An utterance is closed after this long even if the speech continues (background talk).</summary>
        public int MaxUtteranceMs { get; set; } = 5000;

        public bool InSpeech { get; private set; }

        /// <summary>Energy of the last frame, dBFS.</summary>
        public double FrameDb { get; private set; } = -100;

        /// <summary>Estimated background noise level (median of recent background frames), dBFS.</summary>
        public double NoiseFloorDb { get; private set; } = -60;

        /// <summary>90th percentile of recent background frames, dBFS (how high the background fluctuates).</summary>
        public double NoisePeakDb { get; private set; } = -60;

        /// <summary>Level a frame must exceed to count as speech, dBFS.</summary>
        public double SpeechThresholdDb => Math.Max(NoiseFloorDb + ContinueMarginDb, NoisePeakDb + ContinueAbovePeakDb);

        private double StartThresholdDb => Math.Max(Math.Max(NoiseFloorDb + StartMarginDb, NoisePeakDb + StartAbovePeakDb), MinSpeechDb);

        /// <summary>True if the last utterance was cut at <see cref="MaxUtteranceMs"/> rather than ending in silence.</summary>
        public bool LastEndedByLength { get; private set; }

        /// <summary>Length of the last utterance including pre-roll, ms.</summary>
        public int LastUtteranceMs { get; private set; }

        private const int NoiseWindowFrames = 150; // 3 s
        private readonly double[] noiseWindow = new double[NoiseWindowFrames];
        private readonly double[] noiseSorted = new double[NoiseWindowFrames];
        private int noiseCount, noiseNext;
        private bool[] recentSpeech = new bool[0];
        private int recentNext, recentSpeechCount, recentFilled;

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
                NoiseFloorDb = NoisePeakDb = Math.Max(db, -90);
                floorInitialised = true;
            }

            if (!InSpeech)
            {
                // the start decision uses the noise model from BEFORE this frame (a speech onset must not raise it)
                var start = StartThresholdDb;
                AddNoiseFrame(db);

                preRoll.Enqueue((bytes, db));
                while (preRoll.Count > PreRollMs / FrameMs) preRoll.Dequeue();

                if (db > start)
                    aboveCount++;
                else
                    aboveCount = 0;

                if (aboveCount >= 3)
                {
                    // the onset frames are speech, not background
                    RemoveLastNoiseFrames(3);
                    InSpeech = true;
                    belowMs = 0;
                    var window = Math.Max(1, HangoverMs / FrameMs);
                    if (recentSpeech.Length != window) recentSpeech = new bool[window];
                    Array.Clear(recentSpeech, 0, recentSpeech.Length);
                    recentNext = recentSpeechCount = recentFilled = 0;
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
            var isSpeech = db > SpeechThresholdDb && db > MinSpeechDb - 4;
            if (isSpeech)
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

            // hang-over window: the utterance ends when (almost) all of the last HangoverMs were quiet. A strict
            // "every frame quiet" rule never fires when the background fluctuates (laptop microphone arrays).
            if (recentFilled == recentSpeech.Length && recentSpeech[recentNext]) recentSpeechCount--;
            recentSpeech[recentNext] = isSpeech;
            if (isSpeech) recentSpeechCount++;
            recentNext = (recentNext + 1) % recentSpeech.Length;
            if (recentFilled < recentSpeech.Length) recentFilled++;
            var quiet = belowMs >= HangoverMs ||
                        (recentFilled == recentSpeech.Length && !isSpeech &&
                         recentSpeechCount <= Math.Max(1, (int)(EndSpeechFraction * recentSpeech.Length)));

            if (quiet)
            {
                End(speechMs >= MinSpeechMs);
            }
            else if (uttMs >= MaxUtteranceMs)
            {
                // continuous "speech" for the maximum length: most likely a new, louder background (a fan was
                // switched on, people talking). Learn it as background so the detector does not re-trigger forever.
                foreach (var f in utteranceDb) AddNoiseFrame(f);
                End(speechMs >= MinSpeechMs, true);
            }
        }

        private void AddNoiseFrame(double db)
        {
            noiseWindow[noiseNext] = Math.Max(db, -90);
            noiseNext = (noiseNext + 1) % NoiseWindowFrames;
            if (noiseCount < NoiseWindowFrames) noiseCount++;
            Array.Copy(noiseWindow, noiseSorted, noiseCount);
            Array.Sort(noiseSorted, 0, noiseCount);
            NoiseFloorDb = noiseSorted[noiseCount / 2];
            NoisePeakDb = noiseSorted[Math.Min(noiseCount - 1, noiseCount * 9 / 10)];
        }

        private void RemoveLastNoiseFrames(int n)
        {
            n = Math.Min(n, noiseCount - 1); // keep at least one frame
            for (var i = 0; i < n; i++)
            {
                noiseNext = (noiseNext - 1 + NoiseWindowFrames) % NoiseWindowFrames;
                noiseCount--;
            }

            // the remaining frames are the most recent noiseCount entries ending before noiseNext; when the window
            // has wrapped they are not contiguous from 0, so rebuild a contiguous copy
            var tmp = new double[noiseCount];
            for (var i = 0; i < noiseCount; i++)
                tmp[i] = noiseWindow[(noiseNext - noiseCount + i + 2 * NoiseWindowFrames) % NoiseWindowFrames];
            Array.Copy(tmp, noiseWindow, noiseCount);
            noiseNext = noiseCount % NoiseWindowFrames;
            if (noiseCount == 0) return;
            Array.Copy(noiseWindow, noiseSorted, noiseCount);
            Array.Sort(noiseSorted, 0, noiseCount);
            NoiseFloorDb = noiseSorted[noiseCount / 2];
            NoisePeakDb = noiseSorted[Math.Min(noiseCount - 1, noiseCount * 9 / 10)];
        }

        private void End(bool valid, bool byLength = false)
        {
            InSpeech = false;
            aboveCount = 0;
            LastEndedByLength = byLength;
            LastUtteranceMs = uttMs;
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
