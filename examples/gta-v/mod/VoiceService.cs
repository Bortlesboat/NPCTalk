using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoice
{
    /// <summary>
    /// Runs one line at a time on a background thread: text to the TTS engine, PCM into the player.
    /// The game thread only calls TrySpeak / Cancel / KeepWarm and reads the volatile state.
    ///
    /// Engines that scale to zero (Modal, the fal FishTTS app) take minutes to wake up. Until the
    /// primary engine has answered once, and whenever it doesn't make a sound within
    /// PrimaryFirstAudioMs, lines go to the fallback engine (MiniMax) while the primary wakes up.
    /// </summary>
    internal sealed class VoiceService : IDisposable
    {
        private const int PrimaryFirstAudioMs = 12000;

        private readonly ITts _tts;
        private readonly ITts _fallback;
        private readonly int _keepWarmMs;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private CancellationTokenSource _line;
        private volatile bool _generating;
        private volatile bool _warming;
        private volatile bool _primaryReady;
        private int _lastUseTicks = Environment.TickCount;

        public SpeechPlayer Player { get; } = new SpeechPlayer();

        /// <summary>Errors and status for the game thread to show (it owns the UI).</summary>
        public volatile string Message;

        public VoiceService(ITts tts, ITts fallback, int keepWarmSeconds)
        {
            _tts = tts;
            _fallback = fallback;
            _keepWarmMs = keepWarmSeconds * 1000;
            _primaryReady = fallback == null || !tts.NeedsKeepWarm;
        }

        /// <summary>The primary engine's name (what the ini asked for).</summary>
        public string Engine => _tts.Name;

        /// <summary>Which engine lines go to right now, for the dev panel.</summary>
        public string Speaking => _primaryReady ? _tts.Name : $"{_fallback.Name} ({_tts.Name} waking up)";

        /// <summary>Busy while a line is generating or still audible; NPCs wait their turn.</summary>
        public bool IsBusy => _generating || Player.IsAudible;

        /// <summary>The primary engine answered (e.g. a background warm-up line): use it from now on.</summary>
        public void MarkPrimaryReady()
        {
            if (_primaryReady) return;
            _primaryReady = true;
            Message = $"{_tts.Name} voices are up";
            Log.Write($"{_tts.Name} is ready; lines go to it now");
        }

        /// <summary>
        /// Speak a line. <paramref name="onFirstAudio"/> gets the seconds from now to the first audio
        /// chunk (called on a worker thread).
        /// </summary>
        public bool TrySpeak(string text, Voice voice, Action<double> onFirstAudio = null)
        {
            if (IsBusy || voice == null) return false;
            _generating = true;
            _lastUseTicks = Environment.TickCount;
            var line = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            Interlocked.Exchange(ref _line, line)?.Dispose();
            Player.BeginLine();
            Task.Run(() => RunLine(text, voice, onFirstAudio, line.Token));
            return true;
        }

        /// <summary>Let the engines get this voice ready (MiniMax clones it) before the first line needs it.</summary>
        public void Prepare(Voice voice)
        {
            _tts.Prepare(voice);
            _fallback?.Prepare(voice);
        }

        /// <summary>Stop the current line now.</summary>
        public void Cancel()
        {
            try { _line?.Cancel(); } catch (ObjectDisposedException) { }
            Player.Stop();
        }

        /// <summary>Stop now and drop any session so a more important line can start right away.</summary>
        public void Preempt()
        {
            Cancel();
            _tts.Abort();
        }

        /// <summary>
        /// Send a tiny line in the background when nothing has been said for a while, so an engine that
        /// scales to zero stays up while the game runs. Call every frame.
        /// </summary>
        public void KeepWarm(Func<Voice> anyVoice, bool force = false)
        {
            if (!_tts.NeedsKeepWarm || _warming || _generating) return;
            if (!force && (_keepWarmMs <= 0 || unchecked(Environment.TickCount - _lastUseTicks) < _keepWarmMs)) return;
            Wake(anyVoice());
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            Player.Dispose();
            _tts.Dispose();
            _fallback?.Dispose();
        }

        private void Wake(Voice voice)
        {
            if (voice == null || _warming) return;
            _warming = true;
            _lastUseTicks = Environment.TickCount;
            Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await _tts.SpeakAsync("Hey.", voice, (pcm, rate) => { }, _shutdown.Token).ConfigureAwait(false);
                    Log.Write($"keep-warm {_tts.Name} [{voice.Name}] {sw.Elapsed.TotalSeconds:F1}s");
                    MarkPrimaryReady();
                }
                catch (Exception ex) when (!_shutdown.IsCancellationRequested)
                {
                    Log.Write($"keep-warm {_tts.Name} failed after {sw.Elapsed.TotalSeconds:F1}s: {ex.Message}");
                }
                finally
                {
                    _warming = false;
                }
            });
        }

        private async Task RunLine(string text, Voice voice, Action<double> onFirstAudio, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            bool first = true;
            Action<byte[], int> play = (pcm, rate) =>
            {
                if (ct.IsCancellationRequested) return;
                if (first)
                {
                    first = false;
                    onFirstAudio?.Invoke(sw.Elapsed.TotalSeconds);
                }
                Player.AddPcm(pcm, rate);
            };

            ITts engine = _primaryReady || _fallback == null ? _tts : _fallback;
            try
            {
                if (engine == _tts && _fallback != null)
                {
                    // A primary that went cold (the GPU stopped while the game was paused) would leave the
                    // NPC silent for minutes: give it a few seconds, then say the line with the fallback.
                    using (var firstAudio = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        firstAudio.CancelAfter(PrimaryFirstAudioMs);
                        try
                        {
                            await _tts.SpeakAsync(text, voice, (pcm, rate) =>
                            {
                                if (first) firstAudio.CancelAfter(Timeout.Infinite);
                                play(pcm, rate);
                            }, firstAudio.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (first && !ct.IsCancellationRequested)
                        {
                            Log.Write($"tts {_tts.Name} [{voice.Name}] no audio after {sw.Elapsed.TotalSeconds:F1}s ({ex.GetType().Name}); using {_fallback.Name} while it wakes up");
                            _primaryReady = false;
                            Wake(voice);
                            engine = _fallback;
                            await _fallback.SpeakAsync(text, voice, play, ct).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    await engine.SpeakAsync(text, voice, play, ct).ConfigureAwait(false);
                    if (engine == _fallback) Wake(voice);
                }
                Log.Write($"tts {engine.Name} [{voice.Name}] first audio {(first ? "none" : "ok")}, done {sw.Elapsed.TotalSeconds:F2}s");
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                Player.Stop();
                if (!ct.IsCancellationRequested)
                {
                    engine.Abort();
                    Message = "~r~voice failed:~s~ " + ex.Message;
                    Log.Write($"tts {engine.Name} [{voice.Name}] failed after {sw.Elapsed.TotalSeconds:F2}s: {ex}");
                }
            }
            finally
            {
                Player.EndLine();
                _lastUseTicks = Environment.TickCount;
                _generating = false;
            }
        }
    }
}
