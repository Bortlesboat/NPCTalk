using System;
using System.Collections.Generic;
using System.Linq;
using Keys = System.Windows.Forms.Keys;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

namespace NpcVoice
{
    /// <summary>
    /// Outside dev mode: one key per main character's home ([Keys] VisitMichael/VisitFranklin/VisitTrevor).
    /// It fades out, puts you where they hang out (Michael's and Franklin's living rooms, outside Trevor's
    /// trailer) and has the people who live there standing in front of you, so you talk to them like
    /// anyone else. They're released back to the game once you're far away.
    /// </summary>
    internal sealed class Visits
    {
        private sealed class Place
        {
            public string Name;
            public Keys Key;
            public Vector3 Spot;     // where you stand
            public float Heading;
            public Vector3 Fallback; // outside, if the interior doesn't load
            public string[] Residents;
        }

        private const float ReleaseDistance = 200f;

        private readonly CharacterBook _book;
        private readonly List<Place> _places;
        private readonly List<Ped> _peds = new List<Ped>();
        private Place _current;
        private Action _pending; // teleporting waits, so it runs from Tick
        private int _nextCheck;

        public Visits(CharacterBook book, Keys michael, Keys franklin, Keys trevor)
        {
            _book = book;
            _places = new List<Place>
            {
                new Place { Name = "Michael's house", Key = michael, Spot = new Vector3(-802.31f, 175.06f, 72.84f), Heading = 120f, // lounge (v_michael)
                    Fallback = new Vector3(-852.4f, 160.0f, 65.6f), Residents = new[] { "Michael", "AmandaTownley", "JimmyDisanto", "TracyDisanto" } },
                new Place { Name = "Franklin's house", Key = franklin, Spot = new Vector3(3.2f, 529.8f, 170.9f), Heading = 150f, // main floor (v_franklinshouse, floor z 169.6)
                    Fallback = new Vector3(7.92f, 548.1f, 175.5f), Residents = new[] { "Franklin", "LamarDavis" } },
                new Place { Name = "Trevor's trailer", Key = trevor, Spot = new Vector3(1985.7f, 3812.2f, 32.2f), Heading = 300f,
                    Fallback = new Vector3(1985.7f, 3812.2f, 32.2f), Residents = new[] { "Trevor", "Wade" } },
            };
        }

        public IEnumerable<Ped> Peds => _peds.Where(p => p != null && p.Exists());

        public string KeysHint => string.Join("  ", _places.Select(p => $"~b~{p.Key}~s~ {p.Name}"));

        /// <summary>"at Michael's house" when this spot is at one of the homes, for the LLM's scene.</summary>
        public string PlaceAt(Vector3 pos) =>
            _places.Where(p => p.Spot.DistanceTo(pos) < 45f || p.Fallback.DistanceTo(pos) < 45f).Select(p => "at " + p.Name).FirstOrDefault();

        public bool HandleKey(Keys key)
        {
            Place place = _places.FirstOrDefault(p => p.Key == key);
            if (place == null) return false;
            _pending = () => Go(place);
            return true;
        }

        public void Tick()
        {
            Action pending = _pending;
            _pending = null;
            pending?.Invoke();

            if (_current == null || Game.GameTime < _nextCheck) return;
            _nextCheck = Game.GameTime + 2000;
            if (Game.Player.Character.Position.DistanceTo(_current.Spot) > ReleaseDistance) Release();
        }

        /// <summary>Hand the residents back to the game (it cleans them up like any other ped).</summary>
        public void Release()
        {
            foreach (Ped p in Peds)
            {
                p.AttachedBlip?.Delete();
                p.BlockPermanentEvents = false;
                p.MarkAsNoLongerNeeded();
            }
            _peds.Clear();
            _current = null;
        }

        public void Clear()
        {
            foreach (Ped p in Peds)
            {
                p.AttachedBlip?.Delete();
                p.Delete();
            }
            _peds.Clear();
            _current = null;
        }

        /// <summary>
        /// The director's way in: go to the home whose name contains <paramref name="where"/> ("michael",
        /// "franklin", "trevor") with only <paramref name="only"/> (a character key) there, no help text.
        /// Returns that character's ped, or null.
        /// </summary>
        public Ped GoTo(string where, string only)
        {
            Place place = _places.FirstOrDefault(p => p.Name.IndexOf(where ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            if (place == null) return null;
            Go(place, only, quiet: true);
            Character c = _book.ByKey(only);
            return c == null ? null : World.GetNearbyPeds(Game.Player.Character, 40f)
                .Where(p => p.Model.Hash == c.ModelHash && !p.IsDead)
                .OrderBy(p => p.Position.DistanceTo(Game.Player.Character.Position)).FirstOrDefault();
        }

        private void Go(Place place, string only = null, bool quiet = false)
        {
            Ped player = Game.Player.Character;
            if (player.IsDead) return;
            Screen.FadeOut(400);
            { Script.Wait(450); Heartbeat.Beat(); }
            Clear();

            bool inside = EnterInterior(place.Spot);
            if (!inside && place.Spot != place.Fallback)
            {
                Log.Write($"visit: {place.Name} interior didn't load, going outside instead");
                DevMode.Teleport(place.Fallback, place.Heading);
            }
            Game.Player.WantedLevel = 0;
            player.Heading = place.Heading;

            var spawned = new List<string>();
            var free = FreeSpots(player).GetEnumerator();
            foreach (string key in only != null ? new[] { only } : place.Residents)
            {
                Character c = _book.ByKey(key);
                if (c == null || c.ModelHash == player.Model.Hash) continue;
                if (World.GetNearbyPeds(player, 40f).Any(p => p.Model.Hash == c.ModelHash && !p.IsDead)) // the game already has them here
                {
                    spawned.Add(c.Name + " (already here)");
                    continue;
                }
                if (!free.MoveNext()) break;
                Ped ped = Spawn(c, free.Current, player);
                if (ped != null) spawned.Add(c.Name);
            }
            _current = place;
            _nextCheck = Game.GameTime + 2000;

            Log.Write($"visit: {place.Name} ({(inside ? "inside" : "outside")}) at {player.Position}, interior {Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player)}: {string.Join(", ", spawned)}");
            Screen.FadeIn(600);
            if (!quiet) Screen.ShowHelpText($"~y~{place.Name}~s~~n~Walk up to anyone and talk.~n~{KeysHint}", 10000, true, false);
        }

        /// <summary>Teleport into the interior at this spot and wait for it to load; false if there's none or you'd fall.</summary>
        private static bool EnterInterior(Vector3 spot)
        {
            int interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS, spot.X, spot.Y, spot.Z);
            if (interior == 0)
            {
                DevMode.Teleport(spot, 0f);
                return false;
            }
            Function.Call(Hash.PIN_INTERIOR_IN_MEMORY, interior);
            for (int i = 0; i < 100 && !Function.Call<bool>(Hash.IS_INTERIOR_READY, interior); i++) { Script.Wait(50); Heartbeat.Beat(); }
            DevMode.Teleport(spot, 0f);
            { Script.Wait(300); Heartbeat.Beat(); }
            Ped player = Game.Player.Character;
            SnapToFloor(player, spot);
            return Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player) != 0 && player.HeightAboveGround < 2f;
        }

        /// <summary>Onto the nearest walkable floor (navmesh) if it's close and in the same room, so you don't land in a couch.</summary>
        private static void SnapToFloor(Ped player, Vector3 spot)
        {
            var safe = new OutputArgument();
            if (!Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED, spot.X, spot.Y, spot.Z, true, safe, 0)) return;
            Vector3 floor = safe.GetResult<Vector3>();
            Vector3 to = new Vector3(floor.X, floor.Y, player.Position.Z);
            if (to.DistanceTo(player.Position) < 0.3f || to.DistanceTo(player.Position) > 3f || Math.Abs(floor.Z - player.Position.Z) > 1.5f) return;
            if (World.Raycast(player.Position, to, IntersectFlags.Map, player).DidHit) return; // a wall in between: another room
            player.Position = to;
        }

        /// <summary>Spots in front of you with floor under them and nothing between you and them (walls, furniture).</summary>
        private static IEnumerable<Vector3> FreeSpots(Ped player)
        {
            Vector3 eye = player.Position + new Vector3(0f, 0f, 0.5f);
            foreach (float angle in new[] { 0f, -40f, 40f, -80f, 80f, -120f, 120f, 180f })
            {
                double rad = (player.Heading + angle) * Math.PI / 180.0;
                var dir = new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
                foreach (float distance in new[] { 2.2f, 1.6f })
                {
                    Vector3 spot = player.Position + dir * distance;
                    if (World.Raycast(eye, spot + new Vector3(0f, 0f, 0.5f), IntersectFlags.Everything, player).DidHit) continue;
                    RaycastResult floor = World.Raycast(spot + new Vector3(0f, 0f, 0.5f), spot - new Vector3(0f, 0f, 2.5f), IntersectFlags.Everything, player);
                    if (!floor.DidHit || Math.Abs(floor.HitPosition.Z + 1f - player.Position.Z) > 1.2f) continue;
                    yield return new Vector3(spot.X, spot.Y, floor.HitPosition.Z + 1f);
                    break;
                }
            }
        }

        private Ped Spawn(Character c, Vector3 pos, Ped player)
        {
            var model = new Model(c.ModelHash);
            if (!model.Request(3000))
            {
                Notification.Show($"NpcVoice: couldn't load the model for {c.Name}");
                return null;
            }
            Vector3 toPlayer = player.Position - pos;
            Ped ped = World.CreatePed(model, pos, (float)(Math.Atan2(-toPlayer.X, toPlayer.Y) * 180.0 / Math.PI));
            model.MarkAsNoLongerNeeded();
            if (ped == null) return null;

            Function.Call(Hash.SET_PED_DEFAULT_COMPONENT_VARIATION, ped);
            ped.IsPersistent = true;
            ped.BlockPermanentEvents = true; // they're home: no wandering off, no panicking at your car alarm
            ped.Task.StandStill(-1);
            ped.Task.LookAt(player, -1);

            Blip blip = ped.AddBlip();
            blip.Name = c.Name;
            blip.Color = BlipColor.Yellow;
            blip.Scale = 0.7f;
            blip.IsShortRange = true;
            _peds.Add(ped);
            return ped;
        }
    }
}
