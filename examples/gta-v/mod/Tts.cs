using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NpcVoice
{
    /// <summary>A text-to-speech engine that speaks in the voice of a reference clip.</summary>
    internal interface ITts : IDisposable
    {
        string Name { get; }

        /// <summary>Whether the engine scales to zero when idle, so a tiny request now and then keeps it warm.</summary>
        bool NeedsKeepWarm { get; }

        /// <summary>Speak one line; <paramref name="onAudio"/> gets 16-bit mono PCM chunks in order.</summary>
        Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct);

        /// <summary>Drop any open session so the next line starts clean.</summary>
        void Abort();

        /// <summary>Get ready to speak in this voice (e.g. clone it) before the first line needs it.</summary>
        void Prepare(Voice voice);
    }

    internal static class Tts
    {
        /// <summary>
        /// [Tts] Engine in NpcVoice.ini: modal (the repo's Fish S2 Pro server on Modal), fish (the
        /// FishTTS app on fal), minimax or lux.
        /// </summary>
        public static ITts Create(string engine, string fishAppId, string fishPath, int luxRefSeconds, string modalUrl = null, string modalToken = null, int bestOf = 1)
        {
            switch ((engine ?? "").Trim().ToLowerInvariant())
            {
                case "modal": return new ModalTts(modalUrl, modalToken, bestOf);
                case "fish": return new FishTts(fishAppId, fishPath);
                case "minimax": return new MiniMaxTts();
                default: return new LuxTts(luxRefSeconds);
            }
        }
    }

    /// <summary>Delivery tags like [angry] at the start of a line: Fish voices them, the others only use them as hints.</summary>
    internal static class Speech
    {
        private static readonly Regex LeadingTag = new Regex(@"^\s*\[([^\]]+)\]");

        public static string Tag(string text)
        {
            Match m = LeadingTag.Match(text ?? "");
            return m.Success ? m.Groups[1].Value.Trim().ToLowerInvariant() : null;
        }

        public static string Plain(string text) => Regex.Replace(text ?? "", @"\[[^\]]*\]\s*", "").Trim();

        /// <summary>Which mood folder of a voice fits the tag (voices/&lt;name&gt;/angry, /drunk), if any.</summary>
        public static string Mood(string tag)
        {
            if (tag == null) return null;
            if (Regex.IsMatch(tag, "angry|shout|yell|furious|scream|rage|annoyed|mad")) return "angry";
            if (Regex.IsMatch(tag, "drunk|slur|tipsy")) return "drunk";
            return null;
        }
    }

    /// <summary>
    /// The repo's own FishTTS app (server/fal/fal_app.py) over its /realtime WebSocket: streams PCM as it is
    /// generated, ~0.3-0.5 s to first audio when warm. Needs `fal deploy server/fal/fal_app.py::FishTTS`.
    /// </summary>
    internal sealed class FishTts : ITts
    {
        private readonly FalRealtimeClient _client;

        public FishTts(string appId, string path)
        {
            if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("[Tts] Engine = fish needs [Fal] AppId");
            _client = new FalRealtimeClient(appId, FalApi.Key, path);
        }

        public string Name => "fish";
        public bool NeedsKeepWarm => true;

        public async Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct)
        {
            bool gotAudio = false;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await _client.SpeakAsync(text, voice, (pcm, rate) => { gotAudio = true; onAudio(pcm, rate); }, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception) when (attempt == 0 && !gotAudio && !ct.IsCancellationRequested)
                {
                    _client.Reset(); // idle sessions get closed by fal; reconnect once and resend
                }
            }
        }

        public void Abort() => _client.Reset();
        public void Prepare(Voice voice) { }
        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// The repo's Fish S2 Pro server on Modal (server/s2pro.py): POST /speak streams 16-bit PCM while it
    /// is generated, so playback starts after the first chunk. Clones from the reference clip on every
    /// call and voices [tags]. The GPU stops after 5 idle minutes, hence keep-warm; a cold start
    /// takes a minute or two. With bestOf above 1 the server renders that many takes and returns the
    /// one most like the voice's reference clips, all at once (for lines made ahead, like the director's).
    /// </summary>
    internal sealed class ModalTts : ITts
    {
        private readonly string _url, _token;
        private readonly int _bestOf;

        public ModalTts(string baseUrl, string token, int bestOf = 1)
        {
            _bestOf = Math.Max(1, bestOf);
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("[Tts] Engine = modal needs [Modal] Url");
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("[Tts] Engine = modal needs the NPCVOICE_TOKEN environment variable");
            _url = baseUrl.TrimEnd('/') + "/speak";
            _token = token;
        }

        public string Name => "modal";
        public bool NeedsKeepWarm => true;

        public Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct)
        {
            if (voice.Url == null) throw new IOException($"voice {voice.Name} isn't uploaded yet");
            var body = new Dictionary<string, object>
            {
                ["text"] = text,
                ["voice"] = voice.Name, // prepared on the server at startup (server/s2pro.py /voices); the URL is the fallback
                ["reference_audio_url"] = voice.Url,
                ["reference_text"] = voice.RefText,
            };
            if (_bestOf > 1) body["best_of"] = _bestOf; // server/drama3.py only
            return Net.PostStreamAsync(_url, _token, body, (resp, s) =>
            {
                int rate = resp.Headers.TryGetValues("X-Sample-Rate", out var values) && int.TryParse(values.FirstOrDefault(), out int r) ? r : 44100;
                return WavStream.ReadRawAsync(s, rate, onAudio, ct);
            }, ct, 600);
        }

        public void Abort() { }
        public void Prepare(Voice voice) { }
        public void Dispose() { }
    }

    /// <summary>
    /// fal-ai/lux-tts: zero-shot cloning from the reference clip on every call, no setup and very
    /// cheap. ~1.8 s per line when warm; the runner scales to zero after a few idle minutes, hence keep-warm.
    /// </summary>
    internal sealed class LuxTts : ITts
    {
        private readonly int _refSeconds;

        public LuxTts(int refSeconds) { _refSeconds = Math.Max(3, refSeconds); }

        public string Name => "lux";
        public bool NeedsKeepWarm => true;

        public async Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct)
        {
            if (voice.Url == null) throw new IOException($"voice {voice.Name} isn't uploaded yet");
            var result = await FalApi.RunAsync("fal-ai/lux-tts", new Dictionary<string, object>
            {
                ["prompt"] = Speech.Plain(text),
                ["audio_url"] = voice.Url,
                ["max_ref_length"] = _refSeconds,
                ["num_inference_steps"] = 4,
                ["enable_safety_checker"] = false, // M-rated dialogue; the checker also flags harmless lines
            }, ct).ConfigureAwait(false);
            await FalApi.DownloadAsync(FalApi.FileUrl(result, "audio"), s => WavStream.ReadAsync(s, onAudio, ct), ct).ConfigureAwait(false);
        }

        public void Abort() { }
        public void Prepare(Voice voice) { }
        public void Dispose() { }
    }

    /// <summary>
    /// MiniMax Speech 2.8 Turbo on fal. Each voice is cloned once on first use (fal-ai/minimax/voice-clone,
    /// $1.50, needs >= 10 s of audio: clone.wav, else the clips joined) and the id is cached in
    /// minimax_voice.txt next to the clips. No cold starts, ~2.5 s per line, profanity is fine, and
    /// [tags] become MiniMax emotions, so mood folders aren't cloned separately.
    /// </summary>
    internal sealed class MiniMaxTts : ITts
    {
        private const int Rate = 32000;
        private const int HedgeAfterMs = 2500;
        private readonly SemaphoreSlim _cloneLock = new SemaphoreSlim(1, 1);

        public string Name => "minimax";
        public bool NeedsKeepWarm => false;

        public async Task SpeakAsync(string text, Voice voice, Action<byte[], int> onAudio, CancellationToken ct)
        {
            VoiceGroup group = voice.Group.Parent ?? voice.Group;
            for (int attempt = 0; ; attempt++)
            {
                string voiceId = await VoiceIdAsync(group, ct).ConfigureAwait(false);
                var settings = new Dictionary<string, object> { ["voice_id"] = voiceId, ["speed"] = 1.0, ["vol"] = 1.0, ["pitch"] = 0 };
                string emotion = Emotion(Speech.Tag(text));
                if (emotion != null) settings["emotion"] = emotion;
                try
                {
                    var result = await FalApi.RunHedgedAsync("fal-ai/minimax/speech-2.8-turbo", new Dictionary<string, object>
                    {
                        ["prompt"] = Speech.Plain(text),
                        ["voice_setting"] = settings,
                        ["audio_setting"] = new Dictionary<string, object> { ["format"] = "pcm", ["sample_rate"] = Rate, ["channel"] = 1 },
                        ["output_format"] = "url",
                    }, HedgeAfterMs, ct).ConfigureAwait(false);
                    await FalApi.DownloadAsync(FalApi.FileUrl(result, "audio"), s => WavStream.ReadRawAsync(s, Rate, onAudio, ct), ct).ConfigureAwait(false);
                    return;
                }
                catch (FalException ex) when (attempt == 0 && ex.Status >= 400 && ex.Status < 500 && ex.Body.IndexOf("voice", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // MiniMax drops cloned voices that go unused for a week: clone again.
                    Log.Write($"minimax: voice {voiceId} rejected ({ex.Message}); cloning again");
                    group.ClonedId = null;
                    try { File.Delete(IdFile(group)); } catch { }
                }
            }
        }

        /// <summary>
        /// Cached as "id|source" where source names the clip it was cloned from, so a better clone.wav
        /// added later (voices.ts) replaces a clone made from joined clips.
        /// </summary>
        private async Task<string> VoiceIdAsync(VoiceGroup g, CancellationToken ct)
        {
            if (g.ClonedId != null) return g.ClonedId;
            await _cloneLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (g.ClonedId != null) return g.ClonedId;
                string file = IdFile(g);
                string source = g.Clone != null ? SourceTag(g.Clone.WavPath) : "joined";
                if (File.Exists(file))
                {
                    string[] cached = File.ReadAllText(file).Trim().Split('|');
                    if (cached[0].Length > 0 && (cached.Length < 2 || cached[1] == source)) return g.ClonedId = cached[0];
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string url = g.Clone?.Url ?? await FalApi.UploadAsync(g.CloneSourcePath(10.5), "audio/wav").ConfigureAwait(false);
                var result = await FalApi.RunAsync("fal-ai/minimax/voice-clone", new Dictionary<string, object>
                {
                    ["audio_url"] = url,
                    ["model"] = "speech-02-turbo",
                    ["need_volume_normalization"] = true,
                    ["text"] = null, // no preview clip: it only adds latency
                }, ct, 300).ConfigureAwait(false);
                g.ClonedId = (string)result["custom_voice_id"];
                File.WriteAllText(file, g.ClonedId + "|" + source);
                Log.Write($"minimax: cloned {g.Name} from {source} -> {g.ClonedId} in {sw.Elapsed.TotalSeconds:F1}s");
                return g.ClonedId;
            }
            finally
            {
                _cloneLock.Release();
            }
        }

        private static string SourceTag(string path) => Path.GetFileName(path) + ":" + new FileInfo(path).Length;

        /// <summary>Cloning takes ~15-20 s, so start as soon as someone is targeted, not when they first speak.</summary>
        public void Prepare(Voice voice)
        {
            VoiceGroup group = voice?.Group.Parent ?? voice?.Group;
            if (group == null || group.ClonedId != null || group.Preparing) return;
            group.Preparing = true;
            Task.Run(async () =>
            {
                try { await VoiceIdAsync(group, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log.Write($"minimax: clone {group.Name} failed: {ex.Message}"); }
                finally { group.Preparing = false; }
            });
        }

        private static string IdFile(VoiceGroup g) => Path.Combine(g.Dir, "minimax_voice.txt");

        private static string Emotion(string tag)
        {
            if (tag == null) return null;
            if (Speech.Mood(tag) == "angry") return "angry";
            if (Regex.IsMatch(tag, "happy|excit|laugh|cheer|joy")) return "happy";
            if (Regex.IsMatch(tag, "sad|sigh|cry|depress|tired")) return "sad";
            if (Regex.IsMatch(tag, "scared|panic|fear|nervous|terrified")) return "fearful";
            if (Regex.IsMatch(tag, "surpris|shock")) return "surprised";
            if (Regex.IsMatch(tag, "disgust")) return "disgusted";
            return null;
        }

        public void Abort() { }
        public void Dispose() => _cloneLock.Dispose();
    }

    /// <summary>Turns a WAV or raw PCM download into 16-bit mono chunks as the bytes arrive.</summary>
    internal static class WavStream
    {
        private const int Block = 8192;

        public static async Task ReadAsync(Stream s, Action<byte[], int> onAudio, CancellationToken ct)
        {
            byte[] riff = await ReadExactAsync(s, 12, ct).ConfigureAwait(false);
            if (riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F')
                throw new InvalidDataException("TTS result is not a WAV file");

            int format = 1, channels = 1, rate = 44100, bits = 16;
            while (true)
            {
                byte[] head = await ReadExactAsync(s, 8, ct).ConfigureAwait(false);
                string id = System.Text.Encoding.ASCII.GetString(head, 0, 4);
                uint size = BitConverter.ToUInt32(head, 4);
                if (id == "fmt ")
                {
                    byte[] fmt = await ReadExactAsync(s, (int)size + (int)(size & 1), ct).ConfigureAwait(false);
                    format = BitConverter.ToUInt16(fmt, 0);
                    channels = BitConverter.ToUInt16(fmt, 2);
                    rate = BitConverter.ToInt32(fmt, 4);
                    bits = BitConverter.ToUInt16(fmt, 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(fmt, 24); // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    await PumpAsync(s, format, channels, bits, rate, onAudio, ct).ConfigureAwait(false);
                    return;
                }
                else
                {
                    await ReadExactAsync(s, (int)size + (int)(size & 1), ct).ConfigureAwait(false);
                }
            }
        }

        public static Task ReadRawAsync(Stream s, int rate, Action<byte[], int> onAudio, CancellationToken ct) =>
            PumpAsync(s, 1, 1, 16, rate, onAudio, ct);

        private static async Task PumpAsync(Stream s, int format, int channels, int bits, int rate, Action<byte[], int> onAudio, CancellationToken ct)
        {
            int frame = channels * bits / 8;
            if (frame <= 0) throw new InvalidDataException("bad WAV format");
            var buf = new byte[Block + frame];
            int have = 0;
            while (true)
            {
                int n = await s.ReadAsync(buf, have, Block, ct).ConfigureAwait(false);
                if (n <= 0) break;
                have += n;
                int whole = have - have % frame;
                if (whole > 0) onAudio(ToMono16(buf, whole, format, channels, bits), rate);
                Buffer.BlockCopy(buf, whole, buf, 0, have - whole);
                have -= whole;
            }
        }

        private static byte[] ToMono16(byte[] src, int count, int format, int channels, int bits)
        {
            if (format == 1 && bits == 16 && channels == 1)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(src, 0, copy, 0, count);
                return copy;
            }

            int bytesPerSample = bits / 8, frames = count / (bytesPerSample * channels);
            var dst = new byte[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int o = (f * channels + c) * bytesPerSample;
                    if (format == 3 && bits == 32) sum += BitConverter.ToSingle(src, o);
                    else if (bits == 16) sum += BitConverter.ToInt16(src, o) / 32768f;
                    else if (bits == 24) sum += ((src[o] << 8 | src[o + 1] << 16 | src[o + 2] << 24) >> 8) / 8388608f;
                    else if (bits == 32) sum += BitConverter.ToInt32(src, o) / 2147483648f;
                    else if (bits == 8) sum += (src[o] - 128) / 128f;
                }
                float v = Math.Max(-1f, Math.Min(1f, sum / channels));
                short pcm = (short)(v * 32767f);
                dst[2 * f] = (byte)pcm;
                dst[2 * f + 1] = (byte)(pcm >> 8);
            }
            return dst;
        }

        private static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = await s.ReadAsync(buf, off, n - off, ct).ConfigureAwait(false);
                if (r <= 0) throw new EndOfStreamException("TTS audio ended early");
                off += r;
            }
            return buf;
        }

        /// <summary>
        /// Join clips with short pauses, cycling through them until there are at least
        /// <paramref name="minSeconds"/>. Clips in other formats than the first are skipped.
        /// </summary>
        public static string Join(IList<string> wavPaths, double minSeconds, string outPath)
        {
            WaveFormat format = null;
            var pcm = new MemoryStream();
            for (int i = 0; i < wavPaths.Count * 4 && (format == null || pcm.Length < minSeconds * format.AverageBytesPerSecond); i++)
            {
                using (var reader = new WaveFileReader(wavPaths[i % wavPaths.Count]))
                {
                    if (format == null) format = reader.WaveFormat;
                    else if (!reader.WaveFormat.Equals(format)) continue;
                    reader.CopyTo(pcm);
                    pcm.Write(new byte[format.AverageBytesPerSecond * 3 / 10 / format.BlockAlign * format.BlockAlign], 0,
                        format.AverageBytesPerSecond * 3 / 10 / format.BlockAlign * format.BlockAlign);
                }
            }
            if (format == null) throw new IOException("no clips to join");
            using (var w = new WaveFileWriter(outPath, format)) w.Write(pcm.GetBuffer(), 0, (int)pcm.Length);
            return outPath;
        }
    }
}
