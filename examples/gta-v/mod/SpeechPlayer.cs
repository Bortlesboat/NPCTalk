using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NpcVoice
{
    /// <summary>
    /// Plays one streamed line at a time: PCM chunks in from the network thread, volume/pan and the
    /// mouth level read by the game thread. Mono speech is panned to where the NPC is on screen.
    /// </summary>
    internal sealed class SpeechPlayer : IDisposable
    {
        private const int LatencyMs = 120;
        private const int PrebufferMs = 150;

        /// <summary>Peak level per block, delayed by the output latency so lips match what you hear.</summary>
        private sealed class LevelTap : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly Queue<KeyValuePair<DateTime, float>> _levels = new Queue<KeyValuePair<DateTime, float>>();
            private float _current;

            public LevelTap(ISampleProvider source) { _source = source; }
            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int n = _source.Read(buffer, offset, count);
                float peak = 0f;
                for (int i = 0; i < n; i++)
                {
                    float v = Math.Abs(buffer[offset + i]);
                    if (v > peak) peak = v;
                }
                lock (_levels) _levels.Enqueue(new KeyValuePair<DateTime, float>(DateTime.UtcNow.AddMilliseconds(LatencyMs), peak));
                return n;
            }

            public float Level
            {
                get
                {
                    lock (_levels)
                    {
                        while (_levels.Count > 0 && _levels.Peek().Key <= DateTime.UtcNow)
                            _current = _levels.Dequeue().Value;
                        return _current;
                    }
                }
            }
        }

        private sealed class Session
        {
            public BufferedWaveProvider Buffer;
            public LevelTap Tap;
            public PanningSampleProvider Pan;
            public VolumeSampleProvider Volume;
            public WaveOutEvent Out;
            public bool Started;
            public bool StreamDone;
        }

        private readonly object _lock = new object();
        private Session _s;
        private float _volume = 1f, _pan;

        public float Volume
        {
            set { lock (_lock) { _volume = value; if (_s?.Volume != null) _s.Volume.Volume = value; } }
        }

        public float Pan
        {
            set { lock (_lock) { _pan = value; if (_s?.Pan != null) _s.Pan.Pan = value; } }
        }

        public float Level
        {
            get { lock (_lock) return _s?.Started == true ? _s.Tap.Level : 0f; }
        }

        /// <summary>True from the first audible sample until the buffer has drained.</summary>
        public bool IsAudible
        {
            get
            {
                lock (_lock)
                    return _s != null && _s.Started && (!_s.StreamDone || _s.Buffer.BufferedBytes > 0);
            }
        }

        public void BeginLine()
        {
            lock (_lock)
            {
                Teardown();
                _s = new Session();
            }
        }

        public void AddPcm(byte[] pcm, int sampleRate)
        {
            lock (_lock)
            {
                var s = _s;
                if (s == null || s.StreamDone) return;
                if (s.Buffer == null)
                {
                    s.Buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
                    {
                        BufferDuration = TimeSpan.FromMinutes(1),
                        DiscardOnBufferOverflow = true,
                    };
                    s.Tap = new LevelTap(s.Buffer.ToSampleProvider());
                    s.Pan = new PanningSampleProvider(s.Tap) { Pan = _pan };
                    s.Volume = new VolumeSampleProvider(s.Pan) { Volume = _volume };
                    s.Out = new WaveOutEvent { DesiredLatency = LatencyMs };
                    s.Out.Init(s.Volume);
                }
                s.Buffer.AddSamples(pcm, 0, pcm.Length);
                if (!s.Started && s.Buffer.BufferedDuration.TotalMilliseconds >= PrebufferMs) Play(s);
            }
        }

        public void EndLine()
        {
            lock (_lock)
            {
                if (_s == null) return;
                _s.StreamDone = true;
                if (!_s.Started && _s.Buffer != null && _s.Buffer.BufferedBytes > 0) Play(_s);
            }
        }

        public void Stop()
        {
            lock (_lock) Teardown();
        }

        public void Dispose() => Stop();

        private static void Play(Session s)
        {
            s.Started = true;
            s.Out.Play();
        }

        private void Teardown()
        {
            if (_s == null) return;
            _s.StreamDone = true;
            _s.Out?.Stop();
            _s.Out?.Dispose();
            _s = null;
        }
    }
}
