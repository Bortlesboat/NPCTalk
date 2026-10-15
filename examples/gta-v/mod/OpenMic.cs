using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace NpcVoice
{
    /// <summary>
    /// Always-on mic for proximity chat. Listens all the time, finds where you start and stop talking
    /// (loudness against a running estimate of the background noise) and hands each utterance over as
    /// a 16 kHz WAV. While <paramref name="paused"/> says so (an NPC is talking, push-to-talk is held,
    /// a reply is being written) it drops what it hears, so the NPC's own voice coming out of the
    /// speakers never gets transcribed as yours.
    /// </summary>
    internal sealed class OpenMic : IDisposable
    {
        private const int Rate = 16000, BufferMs = 30;
        private const int StartMs = 150;     // this much loud audio starts an utterance
        private const int PrerollMs = 300;   // kept from before the start so the first syllable isn't cut
        private const int MinSpeechMs = 450; // shorter blips (a cough, a click) are dropped
        private const int MaxMs = 12000;    // this long without a pause is noise (a loud game, music), not someone talking
        private const float MinThresholdDb = -50f;

        private readonly float _marginDb;
        private readonly int _silenceMs;
        private readonly Func<bool> _paused;
        private readonly Action<byte[], double, float> _onUtterance;
        private readonly Queue<byte[]> _preroll = new Queue<byte[]>();
        private const int FloorWindow = 50;  // 1.5 s of 30 ms blocks
        private readonly float[] _recentDb = new float[FloorWindow];
        private readonly float[] _sorted = new float[FloorWindow];
        private WaveInEvent _in;
        private MemoryStream _speech;
        private int _prerollBytes, _loudMs, _quietMs, _speechMs, _recentCount, _recentNext;
        private float _peak;

        /// <summary>Muted by the player (toggle key); nothing is recorded.</summary>
        public volatile bool Muted;

        /// <summary>Peak of the last 30 ms, 0..1, for the HUD meter.</summary>
        public volatile float Level;

        /// <summary>Inside an utterance right now.</summary>
        public volatile bool Hearing;

        public bool IsRunning => _in != null;

        /// <param name="marginDb">How far above the background noise counts as talking.</param>
        /// <param name="silenceMs">How long a pause ends what you're saying.</param>
        /// <param name="onUtterance">WAV, seconds, peak; called on the capture thread.</param>
        public OpenMic(float marginDb, int silenceMs, Func<bool> paused, Action<byte[], double, float> onUtterance)
        {
            _marginDb = marginDb;
            _silenceMs = silenceMs;
            _paused = paused;
            _onUtterance = onUtterance;
        }

        public void Start()
        {
            if (_in != null) return;
            _in = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = BufferMs };
            _in.DataAvailable += OnData;
            _in.StartRecording();
        }

        public void Dispose()
        {
            var input = _in;
            _in = null;
            if (input == null) return;
            try { input.StopRecording(); } catch { }
            input.Dispose();
        }

        private void OnData(object sender, WaveInEventArgs e) => Feed(e.Buffer, e.BytesRecorded);

        /// <summary>One block of 16 kHz 16-bit mono audio (from the mic, or a test).</summary>
        internal void Feed(byte[] buffer, int bytesRecorded)
        {
            int samples = bytesRecorded / 2;
            if (samples == 0) return;
            double sum = 0;
            int peak = 0;
            for (int i = 0; i < samples; i++)
            {
                int v = BitConverter.ToInt16(buffer, 2 * i);
                sum += (double)v * v;
                if (v < 0) v = -v;
                if (v > peak) peak = v;
            }
            float db = (float)(10 * Math.Log10(sum / samples / (32768.0 * 32768.0) + 1e-12));
            Level = peak / 32768f;
            int ms = samples * 1000 / Rate;

            if (Muted || _paused())
            {
                Reset();
                _preroll.Clear();
                _prerollBytes = 0;
                return;
            }

            var buf = new byte[bytesRecorded];
            Buffer.BlockCopy(buffer, 0, buf, 0, bytesRecorded);
            bool loud = db > Math.Max(NoiseFloor(db) + _marginDb, MinThresholdDb);

            if (_speech == null)
            {
                _preroll.Enqueue(buf);
                _prerollBytes += buf.Length;
                while (_prerollBytes > PrerollMs * Rate / 1000 * 2) _prerollBytes -= _preroll.Dequeue().Length;

                _loudMs = loud ? _loudMs + ms : 0;
                if (_loudMs >= StartMs) Begin();
                return;
            }

            _speech.Write(buf, 0, buf.Length);
            _speechMs += ms;
            if (peak / 32768f > _peak) _peak = peak / 32768f;
            _quietMs = loud ? 0 : _quietMs + ms;
            if (_quietMs >= _silenceMs || _speechMs >= MaxMs) End();
        }

        /// <summary>
        /// Background level: the quietest blocks of the last 1.5 s (10th percentile). Speech always has
        /// gaps between words, so this stays at the noise level while you talk, and a game or room that
        /// gets louder raises it within 1.5 s.
        /// </summary>
        private float NoiseFloor(float db)
        {
            _recentDb[_recentNext] = db;
            _recentNext = (_recentNext + 1) % FloorWindow;
            if (_recentCount < FloorWindow) _recentCount++;
            Array.Copy(_recentDb, _sorted, _recentCount);
            Array.Sort(_sorted, 0, _recentCount);
            return _recentCount < 10 ? -60f : _sorted[_recentCount / 10];
        }

        private void Begin()
        {
            _speech = new MemoryStream();
            _speechMs = 0;
            foreach (byte[] b in _preroll)
            {
                _speech.Write(b, 0, b.Length);
                _speechMs += b.Length / 2 * 1000 / Rate;
            }
            _preroll.Clear();
            _prerollBytes = 0;
            _quietMs = 0;
            _peak = Level;
            Hearing = true;
        }

        private void End()
        {
            MemoryStream speech = _speech;
            int spoken = _speechMs - _quietMs;
            float peak = _peak;
            bool noise = _speechMs >= MaxMs;
            Reset();
            if (spoken < MinSpeechMs || noise) return;

            byte[] pcm = speech.ToArray();
            var wav = new MemoryStream();
            using (var w = new WaveFileWriter(new IgnoreDisposeStream(wav), new WaveFormat(Rate, 16, 1)))
                w.Write(pcm, 0, pcm.Length);
            _onUtterance(wav.ToArray(), pcm.Length / 2.0 / Rate, peak);
        }

        private void Reset()
        {
            _speech = null;
            _loudMs = _quietMs = _speechMs = 0;
            _peak = 0f;
            Hearing = false;
        }
    }
}
