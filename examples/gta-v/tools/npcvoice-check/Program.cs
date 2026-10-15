// Exercises the mod's network path outside the game: upload clips, LLM reply, TTS, STT of the result.
// LLM and speech-to-text settings come from examples/gta-v/mod/NpcVoice.ini, read the way ScriptHookVDotNet reads it.
//   npcvoice-check <voicesDir> <engine> <voiceName> [line typed by the player]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NpcVoice
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Mimic GTA5.exe: start from the legacy TLS default, then let Net.Init fix it.
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls;
            Net.Init();
            if (args[0] == "vad") return await Vad(args[1]);
            if (args[0] == "actions") return await Actions();
            if (args[0] == "talk") return await Talk(args.Skip(1).ToArray());
            if (args[0] == "record") return Record(args[1], args.Length > 2 ? int.Parse(args[2]) : 3600);
            string root = args[0], engine = args[1], voiceName = args[2];
            string said = args.Length > 3 ? args[3] : "Yo Franklin, you got a minute? I need a ride to the airport.";
            Log.Init(Path.Combine(Path.GetTempPath(), "npcvoice-harness.log"));
            FalApi.Key = Environment.GetEnvironmentVariable("FAL_KEY", EnvironmentVariableTarget.User);
            string llmKey = Environment.GetEnvironmentVariable("CEREBRAS_API_KEY", EnvironmentVariableTarget.User);

            VoiceGroup.PreferLongReference = engine == "modal" || engine == "fish";
            var lib = new VoiceLibrary(root, new[] { "Hillbilly", "Marine" }, new[] { "FemaleCop" }, w => Console.WriteLine("warn: " + w));
            Console.WriteLine($"{lib.Groups.Count} voices, {lib.ClipCount} clips: {string.Join(", ", lib.Groups.Select(g => g.Name + (g.Moods.Count > 0 ? "+" + string.Join("+", g.Moods.Keys) : "")))}");
            var sw = Stopwatch.StartNew();
            await lib.UploadAllAsync(w => Console.WriteLine("warn: " + w));
            Console.WriteLine($"uploaded {lib.Uploaded}/{lib.ClipCount} in {sw.Elapsed.TotalSeconds:F1}s");

            string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\mod\NpcVoice.ini");
            var ini = ShvdnIni.Load(iniPath);
            string llmUrl = Net.HttpsUrl(ini.Get("Llm", "BaseUrl", "api.cerebras.ai/v1"), "https://api.cerebras.ai/v1");
            string llmModel = ini.Get("Llm", "Model", "qwen-3.8-27b");
            string sttEndpoint = ini.Get("Stt", "Endpoint", "nvidia/nemotron-asr-multilingual/asr");
            Console.WriteLine($"ini {Path.GetFullPath(iniPath)}: llm {llmModel} at {llmUrl}, stt {sttEndpoint}");

            var llm = new LlmClient(llmUrl, llmKey, llmModel, 120, 0.9, "", true);
            var msgs = new List<ChatMessage>
            {
                new ChatMessage("system", "You are Franklin Clinton in GTA V, speaking out loud. Reply with ONLY the words you say, one or two short sentences, South LA slang. You may start with ONE tag like [angry] or [laughing]."),
                new ChatMessage("user", "Michael says to you: " + said),
            };
            string reply = null;
            for (int i = 0; i < 3; i++)
            {
                sw.Restart();
                reply = await llm.CompleteAsync(msgs, CancellationToken.None);
                Console.WriteLine($"llm {sw.Elapsed.TotalSeconds:F2}s: {reply}");
            }

            string modalUrl = Net.HttpsUrl(ini.Get("Modal", "Url", ""), "");
            ITts tts = Tts.Create(engine, "", "realtime", 10, modalUrl, Environment.GetEnvironmentVariable("NPCVOICE_TOKEN", EnvironmentVariableTarget.User));
            Voice voice = VoiceLibrary.Pick(lib.Find(voiceName), 0, Speech.Mood(Speech.Tag(reply)));
            Console.WriteLine($"voice {voice?.Name} (tag {Speech.Tag(reply)})");
            foreach (string line in new[] { "Hey.", reply, "[angry] Man, get the hell outta my face before I do somethin' stupid, dawg!" })
            {
                Voice v = VoiceLibrary.Pick(lib.Find(voiceName), 0, Speech.Mood(Speech.Tag(line)));
                var pcm = new MemoryStream();
                int rate = 0;
                double first = -1;
                sw.Restart();
                await tts.SpeakAsync(line, v, (b, r) => { if (first < 0) first = sw.Elapsed.TotalSeconds; rate = r; pcm.Write(b, 0, b.Length); }, CancellationToken.None);
                double audio = pcm.Length / 2.0 / Math.Max(1, rate);
                string outPath = Path.Combine(Path.GetTempPath(), $"npcvoice_{engine}_{voiceName}_{Math.Abs(line.GetHashCode()) % 1000}.wav");
                using (var w = new WaveFileWriter(outPath, new WaveFormat(rate, 16, 1))) w.Write(pcm.ToArray(), 0, (int)pcm.Length);
                Console.WriteLine($"tts [{v.Name}] first audio {first:F2}s, done {sw.Elapsed.TotalSeconds:F2}s, {audio:F1}s of audio @ {rate} Hz -> {outPath}");

                // Speech-to-text of what we generated: checks intelligibility and the STT path in one go.
                var stt = new FalSpeechToText(sttEndpoint, FalApi.Key);
                byte[] wav16 = To16k(outPath);
                sw.Restart();
                string heard = await stt.TranscribeAsync(wav16, CancellationToken.None);
                Console.WriteLine($"  stt {sw.Elapsed.TotalSeconds:F2}s: {heard}");
            }
            return 0;
        }

        /// <summary>
        /// npcvoice-check vad &lt;voicesDir&gt;: plays real lines into the open-mic detector over quiet and
        /// loud background noise, in 30 ms blocks like the mic, and transcribes what it cuts out.
        /// </summary>
        private static async Task<int> Vad(string root)
        {
            FalApi.Key = Environment.GetEnvironmentVariable("FAL_KEY", EnvironmentVariableTarget.User);
            var stt = new FalSpeechToText("nvidia/nemotron-asr-multilingual/asr", FalApi.Key);
            short[] Speech(string voice) => Samples(To16k(Path.Combine(root, voice, "ref_0.wav")));
            var rng = new Random(1);
            short[] Noise(double seconds, double dbfs)
            {
                var s = new short[(int)(seconds * 16000)];
                double amp = Math.Pow(10, dbfs / 20) * 32768 * 1.7; // uniform noise: rms = amp / sqrt(3)
                for (int i = 0; i < s.Length; i++) s[i] = (short)((rng.NextDouble() * 2 - 1) * amp);
                return s;
            }
            short[] Mix(short[] a, short[] noise) => a.Select((v, i) => (short)Math.Max(-32768, Math.Min(32767, v + noise[i % noise.Length]))).ToArray();

            var scenes = new (string Name, short[][] Parts)[]
            {
                ("quiet room", new[] { Noise(2, -62), Mix(Speech("Franklin"), Noise(1, -62)), Noise(2, -62), Mix(Speech("LamarDavis"), Noise(1, -62)), Noise(2, -62) }),
                ("loud game in the background (-35 dBFS)", new[] { Noise(3, -35), Mix(Speech("Trevor"), Noise(1, -35)), Noise(3, -35) }),
                ("loud noise, nobody talking", new[] { Noise(2, -62), Noise(6, -30), Noise(2, -62) }),
            };
            foreach (var (name, parts) in scenes)
            {
                var found = new List<Tuple<byte[], double, float>>();
                var mic = new OpenMic(12f, 700, () => false, (wav, s, p) => found.Add(Tuple.Create(wav, s, p)));
                short[] all = parts.SelectMany(p => p).ToArray();
                for (int i = 0; i + 480 <= all.Length; i += 480) // 30 ms blocks
                {
                    var block = new byte[960];
                    Buffer.BlockCopy(all, i * 2, block, 0, 960);
                    mic.Feed(block, 960);
                }
                Console.WriteLine($"== {name}: {found.Count} utterance(s) in {all.Length / 16000.0:F1}s of audio");
                foreach (var u in found)
                    Console.WriteLine($"   {u.Item2:F1}s peak {u.Item3:F2}: {await stt.TranscribeAsync(u.Item1, CancellationToken.None)}");
            }
            return 0;
        }

        /// <summary>
        /// npcvoice-check actions: asks Jev (TYPESAFE_API_KEY) the mod's action question for typical
        /// exchanges and prints its pick, probability, whether the mod would act on it, and the latency.
        /// </summary>
        private static async Task<int> Actions()
        {
            var jev = new JevClient("https://api.typesafe.ai", Environment.GetEnvironmentVariable("TYPESAFE_API_KEY", EnvironmentVariableTarget.User), "jev-latest");
            if (!jev.IsConfigured) { Console.WriteLine("set TYPESAFE_API_KEY first"); return 1; }
            var cases = new (string Npc, string Gender, string Said, string Reply, string Expect)[]
            {
                ("Lamar Davis", "man", "yo what's up homie", "Ayy, my boy Franklin! Where you been at, dog? Come here, man.", "hug / none"),
                ("Lamar Davis", "man", "you look like a crackhead, nobody likes your raps", "Say that shit again, bitch, I dare you. I'll slap the taste out your mouth.", "slap"),
                ("Trevor Philips", "man", "Trevor you're a smelly redneck meth head", "I'm gonna beat you so hard your grandkids are gonna feel it, come here!", "punch"),
                ("Trevor Philips", "man", "I'm gonna rob you old man", "Rob me? Sweetheart, I'll put a bullet in your fucking skull right now.", "shoot / threaten_gun"),
                ("Michael De Santa", "man", "give me your wallet", "Don't you move. You so much as twitch and I'll paint the sidewalk with you.", "threaten_gun"),
                ("Amanda De Santa", "woman", "you're so beautiful, come here", "Oh, you're sweet. Come here, handsome, give me a little kiss.", "kiss"),
                ("Jimmy De Santa", "man", "I'm gonna kill you, kid", "Whoa, whoa, please, man! Please don't kill me, I'll do anything, I swear!", "kneel_beg"),
                ("Hillbilly", "man", "you want to fight, redneck?", "Oh, you wanna go, boy? Hold on, lemme get this shirt off first.", "shirt_off"),
                ("Lamar Davis", "man", "kiss my ass", "Nah homie, YOU kiss MY ass, look at this, right here, all yours.", "bend_over"),
                ("Trevor Philips", "man", "drop your pants, I dare you", "You dare me? Oh, you don't know who you're fucking with. There, happy?", "pants_off"),
                ("Female Cop", "woman", "fuck you pig", "Yeah? Fuck you too, asshole. Have a great day.", "middle_finger"),
                ("U.S. Marine", "man", "the earth is flat bro", "Jesus Christ. How do people like you even dress yourselves in the morning?", "facepalm"),
                ("Hillbilly", "man", "I'll knock you out", "Come on then, boy, put 'em up! Let's see what you got!", "square_up"),
                ("Simeon Yetarian", "man", "there's a bomb in your dealership", "A bomb?! Oh my god, everybody out, run, run!", "run_away"),
                ("Lester Crest", "man", "what's the plan for the heist?", "We go in quiet through the tunnel, grab the gold, and we're out in four minutes.", "none"),
                // Instructions: the character does what they're told, even while grumbling.
                ("Lamar Davis", "man", "take your shirt off", "Man, why you always want me shirtless, dog? Aight, aight, look at these abs.", "shirt_off"),
                ("Trevor Philips", "man", "get on your knees and beg", "Fuck you, Franklin. Fine! Please, please, have mercy on poor old Trevor!", "kneel_beg"),
                ("Amanda De Santa", "woman", "give me a hug", "Ugh, you're so needy. Fine, come here.", "hug"),
                ("Michael De Santa", "man", "pull your gun out", "You sure about that, kid? Alright, there it is.", "threaten_gun"),
                ("Hillbilly", "man", "show me your middle finger", "With pleasure, city boy.", "middle_finger"),
                ("Jimmy De Santa", "man", "drop your pants", "Dude, what is wrong with you? Whatever, happy now?", "pants_off"),
                ("Lamar Davis", "man", "bend over", "What kind of freaky shit is this, homie? Whatever, I got nothing to hide.", "bend_over"),
                ("Trevor Philips", "man", "slap me", "Oh, you asked for it, sweetheart.", "slap"),
                ("Simeon Yetarian", "man", "run away right now", "You don't have to tell me twice, my friend!", "run_away"),
                ("Tracey De Santa", "woman", "kiss me", "Oh my god, you're such a creep. Fine, one kiss.", "kiss"),
            };
            int matches = 0;
            foreach (var t in cases)
            {
                var state = new Dictionary<string, object>
                {
                    ["npc"] = t.Npc, ["npc_gender"] = t.Gender, ["npc_persona"] = "A GTA V character.",
                    ["player"] = "Franklin", ["player_said"] = t.Said, ["npc_reply"] = t.Reply,
                };
                JevChoice c = await jev.ChooseAsync(state, ActionCatalog.Instructions, ActionCatalog.Criteria(), CancellationToken.None);
                ActionCatalog.Kind kind = ActionCatalog.Find(c.Choice);
                bool act = kind != null && kind.Name != "none" && c.Probability >= kind.MinProbability && !(kind.MenOnly && t.Gender == "woman");
                bool match = t.Expect.Split('/').Select(s => s.Trim()).Contains(act ? c.Choice : "none");
                if (match) matches++;
                Console.WriteLine($"{(match ? "ok " : "-- ")}{t.Npc,-18} {c.Choice,-14} {c.Probability,4:P0} conf {c.Confidence:F2} {(act ? "ACT" : "   ")} {c.Seconds:F2}s  expected {t.Expect}");
            }
            Console.WriteLine($"{matches}/{cases.Length} as expected");
            return 0;
        }

        /// <summary>
        /// npcvoice-check talk [Character "what you say"]: the mod's real prompt (rules, persona, a sample
        /// of their game lines from data/lines, a scene) to the LLM in NpcVoice.ini, then Jev's action pick
        /// on the reply. With no arguments it runs everyday talk, insults and instructions.
        /// </summary>
        private static async Task<int> Talk(string[] args)
        {
            string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            var ini = ShvdnIni.Load(Path.Combine(repo, @"mod\NpcVoice.ini"));
            var llm = new LlmClient(Net.HttpsUrl(ini.Get("Llm", "BaseUrl", "api.cerebras.ai/v1"), "https://api.cerebras.ai/v1"),
                Environment.GetEnvironmentVariable("CEREBRAS_API_KEY", EnvironmentVariableTarget.User), ini.Get("Llm", "Model", "qwen-3.8-27b"),
                int.Parse(ini.Get("Llm", "MaxTokens", "160")), double.Parse(ini.Get("Llm", "Temperature", "0.9"), System.Globalization.CultureInfo.InvariantCulture),
                ini.Get("Llm", "ReasoningEffort", ""), ini.Get("Llm", "DisableReasoning", "true").Equals("true", StringComparison.OrdinalIgnoreCase));
            var jev = new JevClient("https://api.typesafe.ai", Environment.GetEnvironmentVariable("TYPESAFE_API_KEY", EnvironmentVariableTarget.User), "jev-latest");

            const string home = "at Michael's house, on Whispymound Dr in Rockford Hills, evening, hot and sunny.";
            const string street = "on Grove St in Davis, afternoon, hot and sunny.";
            var cases = args.Length >= 2
                ? new[] { (Key: args[0], Player: "Franklin", Scene: street, Said: string.Join(" ", args.Skip(1))) }
                : new (string Key, string Player, string Scene, string Said)[]
                {
                    ("Franklin", "Michael", street, "yo what's good, you busy?"),
                    ("Franklin", "Michael", street, "what do you think about lamar"),
                    ("LamarDavis", "Franklin", street, "what's up homie"),
                    ("LamarDavis", "Franklin", street, "your raps are trash and you look like a crackhead"),
                    ("LamarDavis", "Franklin", street, "take your shirt off"),
                    ("Michael", "Franklin", home, "how's the family doing"),
                    ("Michael", "Franklin", home, "get on your knees and beg, old man"),
                    ("Trevor", "Franklin", "outside Trevor's trailer, in Sandy Shores, night.", "you're a smelly redneck meth head"),
                    ("Trevor", "Michael", "outside Trevor's trailer, in Sandy Shores, night.", "give me a hug, T"),
                    ("AmandaTownley", "Michael", home, "where were you all day"),
                    ("JimmyDisanto", "Michael", home, "get off the video games and get a job"),
                    ("TracyDisanto", "Franklin", home, "bend over"),
                    ("LesterCrest", "Michael", "in Murrieta Heights, morning.", "what's the next score"),
                };

            foreach (var t in cases)
            {
                Character c = Dialogue.Load(Path.Combine(repo, @"mod\NpcVoice\characters", t.Key + ".txt"));
                if (c.GameLines.Count == 0) c.GameLines = Dialogue.Tagged(Path.Combine(repo, "data", "lines", c.Voice + ".txt"));
                var msgs = new List<ChatMessage>
                {
                    new ChatMessage("system", Dialogue.SystemPrompt(c, null, false, t.Player, t.Scene)),
                    new ChatMessage("user", Dialogue.Said(t.Player, t.Said)),
                };
                bool female = new[] { "AmandaTownley", "TracyDisanto", "Denise", "Tanisha" }.Contains(t.Key);
                var sw = Stopwatch.StartNew();
                var (reply, order) = await Dialogue.ReplyAsync(llm, jev, msgs, new Dictionary<string, object>
                {
                    ["npc"] = c.Name, ["npc_gender"] = female ? "woman" : "man", ["player"] = t.Player, ["player_said"] = t.Said,
                }, female, t.Player, t.Said, c, CancellationToken.None);
                double llmSeconds = sw.Elapsed.TotalSeconds;
                string action = order != null ? $"  -> told to {order.Choice} {order.Probability:P0}" : "";
                if (order == null && jev.IsConfigured)
                {
                    JevChoice pick = await jev.ChooseAsync(new Dictionary<string, object>
                    {
                        ["npc"] = c.Name, ["npc_gender"] = female ? "woman" : "man", ["npc_persona"] = Net.Truncate(c.Persona, 400),
                        ["player"] = t.Player, ["player_said"] = t.Said, ["npc_reply"] = reply,
                    }, ActionCatalog.Instructions, ActionCatalog.Criteria(), CancellationToken.None);
                    ActionCatalog.Kind kind = ActionCatalog.Find(pick.Choice);
                    action = kind != null && kind.Name != "none" && pick.Probability >= kind.MinProbability ? $"  -> {pick.Choice} {pick.Probability:P0}" : "";
                }
                Console.WriteLine($"{t.Player} to {c.Name} ({c.GameLines.Count} game lines): {t.Said}\n  {llmSeconds:F2}s {reply}{action}\n");
            }
            return 0;
        }

        /// <summary>
        /// npcvoice-check record &lt;out.wav&gt; [max seconds]: everything the PC plays (WASAPI loopback: the game
        /// and the mod's voices) until &lt;out.wav&gt;.stop appears. Silence the device skips is filled in so the
        /// file stays in step with the clock; &lt;out.wav&gt;.start gets the unix ms of its first sample.
        /// </summary>
        private static int Record(string outPath, int maxSeconds)
        {
            string stop = outPath + ".stop";
            if (File.Exists(stop)) File.Delete(stop);
            using (var capture = new NAudio.Wave.WasapiLoopbackCapture())
            using (var writer = new WaveFileWriter(outPath, capture.WaveFormat))
            {
                WaveFormat f = capture.WaveFormat;
                long startMs = 0, written = 0;
                var sync = new object();
                capture.DataAvailable += (s, e) =>
                {
                    lock (sync)
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (startMs == 0) startMs = now - e.BytesRecorded * 1000L / f.AverageBytesPerSecond;
                        long due = (now - startMs) * f.AverageBytesPerSecond / 1000 - e.BytesRecorded;
                        due -= due % f.BlockAlign;
                        if (due - written > f.AverageBytesPerSecond / 10) // more than 0.1 s missing: the device skipped silence
                        {
                            var silence = new byte[due - written];
                            writer.Write(silence, 0, silence.Length);
                            written += silence.Length;
                        }
                        writer.Write(e.Buffer, 0, e.BytesRecorded);
                        written += e.BytesRecorded;
                    }
                };
                capture.StartRecording();
                Console.WriteLine($"recording {f} to {outPath}; create {stop} to stop");
                var sw = Stopwatch.StartNew();
                while (!File.Exists(stop) && sw.Elapsed.TotalSeconds < maxSeconds) Thread.Sleep(200);
                capture.StopRecording();
                Thread.Sleep(500);
                lock (sync) File.WriteAllText(outPath + ".start", startMs.ToString());
                Console.WriteLine($"stopped after {sw.Elapsed.TotalSeconds:F0}s, {written / f.AverageBytesPerSecond}s of sound");
            }
            return 0;
        }

        private static short[] Samples(byte[] wav)
        {
            using (var reader = new WaveFileReader(new MemoryStream(wav)))
            {
                var bytes = new byte[reader.Length];
                int n = reader.Read(bytes, 0, bytes.Length);
                var s = new short[n / 2];
                Buffer.BlockCopy(bytes, 0, s, 0, n);
                return s;
            }
        }

        private static byte[] To16k(string path)
        {
            using (var reader = new WaveFileReader(path))
            {
                var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
                var ms = new MemoryStream();
                using (var w = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(ms), new WaveFormat(16000, 16, 1)))
                {
                    var buf = new float[16000];
                    int n;
                    while ((n = resampled.Read(buf, 0, buf.Length)) > 0) w.WriteSamples(buf, 0, n);
                }
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// Reads an ini the way ScriptHookVDotNet's ScriptSettings does (v3.7), including its quirk of
    /// cutting a value at "//", so settings that break in game break here too.
    /// </summary>
    internal sealed class ShvdnIni
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static ShvdnIni Load(string path)
        {
            var ini = new ShvdnIni();
            string section = "";
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("//")) continue;
                if (line.StartsWith("[") && line.Contains("]")) { section = line.Substring(1, line.IndexOf(']') - 1).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.Contains("//")) value = value.Substring(0, value.IndexOf("//", StringComparison.Ordinal) - 1).TrimEnd();
                if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\"")) value = value.Substring(1, value.Length - 2);
                ini._values[section + "/" + line.Substring(0, eq).Trim()] = value;
            }
            return ini;
        }

        public string Get(string section, string key, string fallback) =>
            _values.TryGetValue(section + "/" + key, out string v) && v.Length > 0 ? v : fallback;
    }
}
