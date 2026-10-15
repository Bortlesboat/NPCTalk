using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTA;

namespace NpcVoice
{
    /// <summary>What the LLM side needs about a ped, captured on the game thread (natives aren't thread-safe).</summary>
    internal struct PedInfo
    {
        public int Handle;
        public int ModelHash;
        public bool Female;

        public static PedInfo Of(Ped p) => new PedInfo { Handle = p.Handle, ModelHash = p.Model.Hash, Female = p.Gender == Gender.Female };
    }

    /// <summary>
    /// Personas from scripts/NpcVoice/characters/:
    ///   <PedHash>.txt           header lines, a "---" line, then the persona/system prompt. Header:
    ///                             name: Display Name
    ///                             voice: folder in voices/ (default: the file name)
    ///                             models: other PedHash names that are this character too (comma separated)
    ///   <PedHash>.examples.txt  optional `situation|line` examples of how they talk (lines/*.txt format)
    ///   _npc.txt                persona for ordinary pedestrians
    /// and lines/<Voice>.txt (your own sample lines): the character's real game lines, `situation|line`; every prompt
    /// gets a fresh random sample so the LLM copies how they actually talk. Prompts are built in Dialogue.
    /// </summary>
    internal sealed class CharacterBook
    {
        private readonly Dictionary<int, Character> _byModel = new Dictionary<int, Character>();
        private readonly Dictionary<int, List<ChatMessage>> _history = new Dictionary<int, List<ChatMessage>>();
        private readonly string _npcPersona;

        public IReadOnlyList<Character> All { get; }

        public CharacterBook(string dir, Action<string> warn)
        {
            var all = new List<Character>();
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir, "*.txt"))
                {
                    string key = Path.GetFileNameWithoutExtension(file);
                    if (key.StartsWith("_") || key.EndsWith(".examples", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Enum.TryParse(key, true, out PedHash model))
                    {
                        warn($"characters/{key}.txt: not a PedHash name; skipped");
                        continue;
                    }

                    var c = Dialogue.Load(file);
                    c.ModelHash = (int)model;
                    _byModel[c.ModelHash] = c;
                    foreach (string extra in c.ExtraModels)
                    {
                        if (Enum.TryParse(extra, true, out PedHash m)) _byModel[(int)m] = c;
                        else warn($"characters/{key}.txt: models: '{extra}' is not a PedHash name");
                    }
                    all.Add(c);
                }
            }
            All = all.OrderBy(c => c.Name).ToList();

            string npc = Path.Combine(dir, "_npc.txt");
            _npcPersona = File.Exists(npc) ? File.ReadAllText(npc).Trim() : Dialogue.DefaultNpcPersona;
        }

        public int GameLineCount => All.Sum(c => c.GameLines.Count);

        public Character Find(int modelHash) => _byModel.TryGetValue(modelHash, out var c) ? c : null;

        public Character ByKey(string key) => All.FirstOrDefault(c => c.Key.Equals(key?.Trim(), StringComparison.OrdinalIgnoreCase));

        public static string PlayerName(Ped player)
        {
            if (player.Model == new Model(PedHash.Franklin)) return "Franklin";
            if (player.Model == new Model(PedHash.Michael)) return "Michael";
            if (player.Model == new Model(PedHash.Trevor)) return "Trevor";
            return "a stranger";
        }

        /// <summary>A line reacting to what's happening (ambient), with no conversation history.</summary>
        public List<ChatMessage> SituationPrompt(PedInfo ped, string situation, string playerName, string scene)
        {
            return new List<ChatMessage>
            {
                new ChatMessage("system", SystemPrompt(ped, playerName, scene)),
                new ChatMessage("user", $"(Situation: {Describe(situation)} Say one short line out loud.)"),
            };
        }

        /// <summary>The player said something to this ped; keeps a short rolling history per ped.</summary>
        public List<ChatMessage> ConversationPrompt(PedInfo ped, string playerName, string said, string scene, string persona = null)
        {
            var messages = new List<ChatMessage> { new ChatMessage("system", SystemPrompt(ped, playerName, scene, persona)) };
            lock (_history)
            {
                if (!_history.TryGetValue(ped.Handle, out var history)) _history[ped.Handle] = history = new List<ChatMessage>();
                history.Add(new ChatMessage("user", Dialogue.Said(playerName, said)));
                if (history.Count > 12) history.RemoveRange(0, history.Count - 12);
                messages.AddRange(history);
            }
            return messages;
        }

        public void Remember(int handle, string reply)
        {
            lock (_history)
                if (_history.TryGetValue(handle, out var history)) history.Add(new ChatMessage("assistant", reply));
        }

        public void Forget(int handle)
        {
            lock (_history) _history.Remove(handle);
        }

        private string SystemPrompt(PedInfo ped, string playerName, string scene, string persona = null) =>
            Dialogue.SystemPrompt(Find(ped.ModelHash), persona ?? _npcPersona, ped.Female, playerName, scene);

        /// <summary>Where and when this is happening, read on the game thread, so replies can react to it.</summary>
        public static string SceneOf(Ped npc, Ped player, string place = null)
        {
            var parts = new List<string>();
            if (place != null) parts.Add(place);
            try
            {
                var pos = npc.Position;
                string zone = World.GetZoneLocalizedName(pos), street = World.GetStreetName(pos);
                if (!string.IsNullOrEmpty(street) && !string.IsNullOrEmpty(zone)) parts.Add($"on {street} in {zone}");
                else if (!string.IsNullOrEmpty(zone)) parts.Add("in " + zone);

                int hour = World.CurrentTimeOfDay.Hours;
                parts.Add(hour < 5 ? "the middle of the night" : hour < 9 ? "early morning" : hour < 12 ? "morning"
                    : hour < 17 ? "afternoon" : hour < 21 ? "evening" : "night");
                string weather = WeatherName(World.Weather);
                if (weather != null) parts.Add(weather);

                if (npc.IsInVehicle()) parts.Add($"you're in a {npc.CurrentVehicle.LocalizedName}");
                if (player.IsInVehicle()) parts.Add($"they're in a {player.CurrentVehicle.LocalizedName}");
                var weapon = player.Weapons.Current;
                if (weapon != null && weapon.Hash != WeaponHash.Unarmed)
                    parts.Add(player.IsAiming ? $"they're aiming a {weapon.LocalizedName} at you" : $"they've got a {weapon.LocalizedName} out");
                int wanted = Game.Player.WantedLevel;
                if (wanted > 0) parts.Add($"they've got {wanted} wanted star{(wanted > 1 ? "s" : "")}, cops are after them");
                if (player.HealthFloat < player.MaxHealthFloat * 0.5f) parts.Add("they look beat up and bloody");
                if (npc.IsInjured || npc.HealthFloat < npc.MaxHealthFloat * 0.5f) parts.Add("you're hurt");
            }
            catch (Exception e)
            {
                Log.Write("scene: " + e.Message);
            }
            return parts.Count == 0 ? null : string.Join(", ", parts) + ".";
        }

        private static string WeatherName(Weather w)
        {
            switch (w)
            {
                case Weather.ExtraSunny: case Weather.Clear: return "hot and sunny";
                case Weather.Raining: return "raining";
                case Weather.ThunderStorm: return "thunderstorm";
                case Weather.Foggy: case Weather.Smog: return "foggy";
                case Weather.Snowing: case Weather.Blizzard: case Weather.Snowlight: case Weather.Christmas: return "snowing";
                default: return null;
            }
        }

        private static string Describe(string situation)
        {
            switch (situation)
            {
                case "fleeing": return "you are running away from danger, terrified.";
                case "threatened": return "the player is pointing a gun at you.";
                case "combat": return "you are in a gunfight or brawl right now.";
                case "hurt": return "you just got hurt.";
                case "driving": return "you are driving and someone is driving like an idiot.";
                case "greet": return "the player just walked right up to you.";
                default: return "you are just going about your day; make a passing remark.";
            }
        }
    }
}
