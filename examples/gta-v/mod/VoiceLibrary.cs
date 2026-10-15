using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NpcVoice
{
    /// <summary>One reference clip and its transcript.</summary>
    internal sealed class Voice
    {
        public string Name;          // "Franklin/ref_0", "Franklin/angry/ref_0"
        public string WavPath;
        public string RefText;
        public VoiceGroup Group;
        public volatile string Url;  // set once uploaded to fal storage
    }

    /// <summary>A folder of clips of one voice in one mood.</summary>
    internal sealed class VoiceGroup
    {
        public string Name;          // "Franklin" or "Franklin/angry"
        public string Dir;
        public VoiceGroup Parent;    // set on mood folders
        public readonly List<Voice> Clips = new List<Voice>();
        public Voice Clone;          // optional clone.wav (>= 10 s) for engines that clone a voice once
        public readonly Dictionary<string, VoiceGroup> Moods = new Dictionary<string, VoiceGroup>(StringComparer.OrdinalIgnoreCase);
        public volatile string ClonedId;
        public volatile bool Preparing;

        /// <summary>Engines that clone from the clip on every request (Fish) sound closer with 10-30 s of reference.</summary>
        public static bool PreferLongReference;

        /// <summary>
        /// The clip this voice speaks with: clone.wav (~25 s of lines) for Fish, otherwise the first
        /// uploaded clip (`npctalk voice make` writes the best as ref_0). One clip per voice keeps it
        /// consistent, and Fish only has to prepare it once.
        /// </summary>
        public Voice Pick(int seed) =>
            PreferLongReference && Clone?.Url != null ? Clone : Clips.FirstOrDefault(v => v.Url != null);

        /// <summary>The clip to clone this voice from: clone.wav, else the clips joined to at least <paramref name="minSeconds"/>.</summary>
        public string CloneSourcePath(double minSeconds) => Clone?.WavPath ??
            WavStream.Join(Clips.Select(c => c.WavPath).ToList(), minSeconds, Path.Combine(Dir, "clone_joined.wav"));
    }

    /// <summary>
    /// Reference voices from scripts/NpcVoice/voices/ (made by voices.ts from the YouTube playlist):
    ///   &lt;Name&gt;/ref_*.wav + .lab        clips of that voice (one is picked per NPC)
    ///   &lt;Name&gt;/clone.wav + .lab        optional >= 10 s clip for MiniMax cloning
    ///   &lt;Name&gt;/angry/, &lt;Name&gt;/drunk/   optional mood variants, used for lines tagged [angry] / [drunk]
    /// Characters pick a voice by name (characters/*.txt "voice:"), everyone else gets a gender pool.
    /// </summary>
    internal sealed class VoiceLibrary
    {
        private static readonly string[] MoodNames = { "angry", "drunk" };
        private readonly Dictionary<string, VoiceGroup> _groups = new Dictionary<string, VoiceGroup>(StringComparer.OrdinalIgnoreCase);
        private readonly List<VoiceGroup> _male = new List<VoiceGroup>();
        private readonly List<VoiceGroup> _female = new List<VoiceGroup>();

        public IReadOnlyCollection<VoiceGroup> Groups => _groups.Values;

        public VoiceLibrary(string root, IEnumerable<string> malePool, IEnumerable<string> femalePool, Action<string> warn)
        {
            if (Directory.Exists(root))
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    VoiceGroup g = LoadGroup(dir, Path.GetFileName(dir), warn);
                    foreach (string mood in MoodNames)
                    {
                        string moodDir = Path.Combine(dir, mood);
                        if (!Directory.Exists(moodDir)) continue;
                        VoiceGroup m = LoadGroup(moodDir, g.Name + "/" + mood, warn);
                        m.Parent = g;
                        if (m.Clips.Count > 0) g.Moods[mood] = m;
                    }
                    if (g.Clips.Count > 0) _groups[g.Name] = g;
                    else warn($"voices/{g.Name}: no ref_*.wav clips with transcripts; skipped");
                }
            }
            AddPool(_male, malePool, warn);
            AddPool(_female, femalePool, warn);
        }

        public int ClipCount => AllClips.Count();
        public int Uploaded => AllClips.Count(v => v.Url != null);

        /// <summary>Every clip including mood variants and clone clips.</summary>
        public IEnumerable<Voice> AllClips =>
            _groups.Values.SelectMany(g => g.Moods.Values.Prepend(g)).SelectMany(g => g.Clone != null ? g.Clips.Append(g.Clone) : g.Clips);

        public VoiceGroup Find(string name) => name != null && _groups.TryGetValue(name, out var g) ? g : null;

        public VoiceGroup Pool(bool female, int seed)
        {
            List<VoiceGroup> pool = female ? _female : _male;
            if (pool.Count == 0) pool = _male.Count > 0 ? _male : _female;
            return pool.Count == 0 ? null : pool[(int)((uint)seed % (uint)pool.Count)];
        }

        /// <summary>A clip from the group, from its mood folder when the line asks for one.</summary>
        public static Voice Pick(VoiceGroup g, int seed, string mood)
        {
            if (g == null) return null;
            if (mood != null && g.Moods.TryGetValue(mood, out var m))
            {
                Voice v = m.Pick(seed);
                if (v != null) return v;
            }
            return g.Pick(seed);
        }

        public Task UploadAllAsync(Action<string> warn)
        {
            return Task.WhenAll(AllClips.Select(async v =>
            {
                try { v.Url = await FalApi.UploadAsync(v.WavPath, "audio/wav").ConfigureAwait(false); }
                catch (Exception ex) { warn($"upload {v.Name}: {ex.Message}"); }
            }));
        }

        private void AddPool(List<VoiceGroup> pool, IEnumerable<string> names, Action<string> warn)
        {
            foreach (string name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                VoiceGroup g = Find(name);
                if (g != null) pool.Add(g);
                else warn($"[Voices] pool voice '{name}' not found in voices/");
            }
        }

        private static VoiceGroup LoadGroup(string dir, string name, Action<string> warn)
        {
            var g = new VoiceGroup { Name = name, Dir = dir };
            foreach (string wav in Directory.GetFiles(dir, "*.wav").OrderBy(p => p))
            {
                string file = Path.GetFileNameWithoutExtension(wav);
                if (file.Equals("clone_joined", StringComparison.OrdinalIgnoreCase)) continue;
                string transcript = new[] { ".lab", ".txt" }.Select(ext => Path.ChangeExtension(wav, ext)).FirstOrDefault(File.Exists);
                if (transcript == null)
                {
                    warn($"voices/{name}/{file}.wav: no .lab/.txt transcript; skipped");
                    continue;
                }
                var v = new Voice { Name = name + "/" + file, WavPath = wav, RefText = File.ReadAllText(transcript).Trim(), Group = g };
                if (file.Equals("clone", StringComparison.OrdinalIgnoreCase)) g.Clone = v;
                else g.Clips.Add(v);
            }
            return g;
        }
    }

    /// <summary>lines.txt in the repo's `situation|text` format; text may start with a Fish tag like [angry].</summary>
    internal sealed class LineBook
    {
        private readonly Dictionary<string, List<string>> _lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Random _rng = new Random();
        private string _last;

        public LineBook(string path)
        {
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int bar = line.IndexOf('|');
                if (line.Length == 0 || line.StartsWith("#") || bar < 0) continue;

                string situation = line.Substring(0, bar).Trim();
                if (!_lines.TryGetValue(situation, out var list)) _lines[situation] = list = new List<string>();
                list.Add(line.Substring(bar + 1).Trim());
            }
        }

        public int Count => _lines.Values.Sum(l => l.Count);

        public string Pick(string situation)
        {
            if (!_lines.TryGetValue(situation, out var list) && !_lines.TryGetValue("chatter", out list)) return null;
            string text;
            int tries = 0;
            do text = list[_rng.Next(list.Count)];
            while (text == _last && list.Count > 1 && ++tries < 5);
            return _last = text;
        }

        public static string Subtitle(string text) => Regex.Replace(text, @"\[.*?\]\s*", "");
    }
}
