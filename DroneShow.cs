using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("DroneShow", "jerky+claude", "1.1.0")]
    [Description("Control drone fleets for formation flight, lights, text/pattern shows, and a wave-based minigame.")]
    public class DroneShow : RustPlugin
    {
        // Drone prefab path (overridable via config)
        private string PrefabDrone => config.DronePrefab;

        // =====================================================================
        //  Permissions
        // =====================================================================
        private const string PermUse = "droneshow.use";    // formation / show control
        private const string PermAdmin = "droneshow.admin"; // config / minigame management

        // =====================================================================
        //  Runtime state
        // =====================================================================
        // group name -> fleet (drones used for formations/shows)
        private readonly Dictionary<string, DroneFleet> _fleets = new Dictionary<string, DroneFleet>();
        // all agents (ticked). They can become Unity-null, so filter every tick.
        private readonly HashSet<DroneAgent> _agents = new HashSet<DroneAgent>();

        private Timer _tickTimer;

        public static DroneShow Instance { get; private set; }

        // =====================================================================
        //  Lifecycle
        // =====================================================================
        private void Init()
        {
            Instance = this;
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
            LoadPatternData();
        }

        private void OnServerInitialized()
        {
            // speed up drone tracking to reduce formation/text drift (global ServerVar)
            Drone.movementSpeedOverride = config.DroneMoveSpeedOverride;
            Drone.altitudeSpeedOverride = config.DroneAltitudeSpeedOverride;

            // update all agents every 0.1s (recompute targetPosition / combat AI)
            _tickTimer = timer.Every(0.1f, TickAll);
        }

        private void Unload()
        {
            _tickTimer?.Destroy();
            // restore drone speed override to default (vanilla)
            Drone.movementSpeedOverride = 0f;
            Drone.altitudeSpeedOverride = 0f;
            // end the minigame & remove bombers
            _game?.Stop();
            _game = null;
            // close any open pattern editor UI
            foreach (var id in _editors.Keys.ToList())
            {
                var pl = BasePlayer.FindByID(id);
                if (pl != null) CuiHelper.DestroyUi(pl, PatternUi);
            }
            _editors.Clear();
            // remove all spawned drones
            foreach (var fleet in _fleets.Values.ToList())
                fleet.DestroyAll();
            _fleets.Clear();
            _agents.Clear();
            Instance = null;
        }

        // =====================================================================
        //  Per-tick processing
        // =====================================================================
        private void TickAll()
        {
            // clean up dead agents while updating
            _agents.RemoveWhere(a => a == null || a.Drone == null || a.Drone.IsDestroyed);
            foreach (var agent in _agents)
                agent.ServerTick();
        }

        public void RegisterAgent(DroneAgent agent) => _agents.Add(agent);
        public void UnregisterAgent(DroneAgent agent) => _agents.Remove(agent);

        // public wrappers so nested classes can use timers safely
        public Timer Every(float interval, Action cb) => timer.Every(interval, cb);
        public Timer Once(float delay, Action cb) => timer.Once(delay, cb);

        public Configuration CfgPublic => config;

        // =====================================================================
        //  Hook: block players from taking control
        // =====================================================================
        private object OnEntityControl(IRemoteControllable controllable, ulong playerId)
        {
            var ent = controllable?.GetEnt();
            if (ent == null) return null;
            var agent = ent.GetComponent<DroneAgent>();
            if (agent != null) return false; // drones managed by this plugin can't be piloted
            return null;
        }

        // =====================================================================
        //  Hook: make show drones invulnerable (config)
        // =====================================================================
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var agent = entity?.GetComponent<DroneAgent>();
            if (agent == null) return null;
            if (agent.Invulnerable)
                return true; // nullify damage
            return null;
        }

        // =====================================================================
        //  Drone spawn helper
        // =====================================================================
        public Drone SpawnDrone(Vector3 position, Quaternion rotation, ulong ownerId, float scale = 1f)
        {
            var ent = GameManager.server.CreateEntity(PrefabDrone, position, rotation) as Drone;
            if (ent == null) return null;
            ent.OwnerID = ownerId;
            // set the scale before Spawn() (clients apply scale at creation time)
            if (scale > 1f)
            {
                ent.transform.localScale = Vector3.one * scale;
                ent.networkEntityScale = true; // sync localScale to clients
            }
            ent.Spawn();
            // give it a random ID so others can't find it from a computer station
            ent.UpdateIdentifier(Guid.NewGuid().ToString("N").Substring(0, 8));
            if (scale > 1f)
            {
                ent.yawSpeed = 2f / scale;   // compensate: scaling up makes yaw faster
                ent.playerCheckRadius = 0f;  // avoid false detection when scaled up
            }
            return ent;
        }

        // Disable drone physical collisions (so they don't knock into each other / terrain and crash).
        // Only Rigidbody contact detection is turned off, so bullets (raycasts) can still hit and down them.
        public void DisableCollisions(Drone drone)
        {
            if (!config.IgnoreDroneCollisions) return;
            if (drone?.body != null)
                drone.body.detectCollisions = false;
        }

        // =====================================================================
        //  Chat commands
        // =====================================================================
        [ChatCommand("drone")]
        private void CmdDrone(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "NoPermission"); return; }
            if (args.Length == 0) { ShowHelp(player); return; }

            string sub = args[0].ToLower();
            var rest = args.Skip(1).ToArray();
            switch (sub)
            {
                case "spawn": HandleSpawn(player, rest); break;
                case "formation": HandleFormation(player, rest); break;
                case "move": HandleMove(player, rest); break;
                case "rotate": HandleRotate(player, rest); break;
                case "scale": HandleScale(player, rest); break;
                case "light": HandleLight(player, rest); break;
                case "show": HandleShow(player, rest); break;
                case "text": HandleText(player, rest); break;
                case "pattern": HandlePattern(player, rest); break;
                case "sequence": HandleSequence(player, rest); break;
                case "orient": HandleOrient(player, rest); break;
                case "spin": HandleSpin(player, rest); break;
                case "present": HandlePresent(player, rest); break;
                case "prefabs": HandlePrefabs(player, rest); break;
                case "list": HandleList(player); break;
                case "clear": HandleClear(player, rest); break;
                default: ShowHelp(player); break;
            }
        }

        private void ShowHelp(BasePlayer player)
        {
            Msg(player, "Help_Drone");
        }

        // ---- spawn ----
        private void HandleSpawn(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Spawn"); return; }
            string name = a[0];
            if (!int.TryParse(a[1], out int count) || count < 1 || count > config.MaxDronesPerGroup)
            { Msg(player, "Spawn_CountRange", config.MaxDronesPerGroup); return; }
            float height = a.Length >= 3 && float.TryParse(a[2], out float h) ? h : config.DefaultSpawnHeight;

            if (_fleets.ContainsKey(name)) { Msg(player, "Group_Exists", name); return; }

            Vector3 center = GetGroundPointInFront(player) + Vector3.up * height;
            var fleet = new DroneFleet(name, (ulong)player.userID);
            fleet.Center = center;
            fleet.Formation = FormationType.Grid;
            fleet.Spacing = config.DefaultSpacing;
            _fleets[name] = fleet;

            for (int i = 0; i < count; i++)
            {
                // spawn already spread across formation slots (prevents overlap = collisions)
                Vector3 local = FormationMath.Slot(FormationType.Grid, i, count, config.DefaultSpacing);
                var agent = AddShowDrone(fleet, i, fleet.WorldSlot(local));
                if (agent != null) agent.LocalSlot = local;
            }
            fleet.RecalculateSlots();
            Msg(player, "Spawn_Done", name, fleet.Count);
        }

        // spawn one formation drone and add it to the fleet
        private DroneAgent AddShowDrone(DroneFleet fleet, int index, Vector3 spawnPos)
        {
            var drone = SpawnDrone(spawnPos, Quaternion.identity, fleet.OwnerId);
            if (drone == null) return null;
            var agent = drone.gameObject.AddComponent<DroneAgent>();
            agent.Init(this, fleet, index);
            agent.Invulnerable = config.ShowDronesInvulnerable;
            drone.targetPosition = spawnPos; // give it a target immediately to prevent falling
            DisableCollisions(drone);
            fleet.Add(agent);
            return agent;
        }

        // ---- text (display characters with the built-in font) ----
        private void HandleText(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Text"); return; }
            var fleet = GetOrCreateFleet(player, a[0], out bool created);
            if (created) Msg(player, "Group_Created", a[0]);
            string message = string.Join(" ", a.Skip(1));
            var rows = Glyphs.TextToRows(message);
            var points = Glyphs.LayoutRows(rows, config.TextDotSpacing);
            ShowPattern(player, fleet, points, Lang("Label_Text", player, message));
        }

        // ---- pattern (display a saved name or a raw array) ----
        private void HandlePattern(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Pattern"); return; }
            var fleet = GetOrCreateFleet(player, a[0], out bool created);
            if (created) Msg(player, "Group_Created", a[0]);
            var extra = a.Skip(1).ToList();
            List<string> rows;
            string label;
            if (extra.Count == 1 && _patternData != null && _patternData.Patterns.TryGetValue(extra[0], out var saved))
            { rows = saved; label = Lang("Label_PatternNamed", player, extra[0]); }
            else
            { rows = extra; label = Lang("Label_Pattern", player); }
            var points = Glyphs.LayoutRows(rows, config.TextDotSpacing);
            ShowPattern(player, fleet, points, label);
        }

        // shared: adjust drone count to need -> place -> light up
        private void ShowPattern(BasePlayer player, DroneFleet fleet, List<Vector3> points, string label)
        {
            if (points.Count == 0) { Msg(player, "NoPoints"); return; }
            if (points.Count > config.MaxDronesPerGroup)
            { Msg(player, "NeedExceedsMaxHint", points.Count, config.MaxDronesPerGroup); return; }

            fleet.StopShow(); // stop any running show/sequence
            AdjustFleetCount(fleet, points.Count);
            fleet.ApplyPoints(points);
            fleet.SetLights(true, config.LightPrefab);
            fleet.RefreshParkedLights(); // turn off lights on surplus drones
            Msg(player, "Text_Displayed", label, points.Count, fleet.Name);
        }

        // ---- sequence (cycle text/formations at a fixed interval) ----
        private void HandleSequence(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Sequence"); return; }

            if (a[1].ToLower() == "off")
            {
                var ex = GetFleet(player, a[0]); if (ex == null) return;
                ex.StopShow(); Msg(player, "Sequence_Stopped", ex.Name); return;
            }
            if (a.Length < 3 || !float.TryParse(a[1], out float interval) || interval < 0.5f)
            { Msg(player, "Usage_Sequence2"); return; }

            var fleet = GetOrCreateFleet(player, a[0], out bool created);
            if (created) Msg(player, "Group_Created", a[0]);

            string joined = string.Join(" ", a.Skip(2));
            var items = joined.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (items.Count == 0) { Msg(player, "Sequence_NeedItems"); return; }

            // parse each item (text may use a flat/up orientation prefix). Also find the max drones needed.
            var parsed = new List<(bool isForm, FormationType ft, List<Vector3> pts, float pitch)>();
            int maxNeeded = 0;
            foreach (var rawItem in items)
            {
                string item = rawItem;
                float pitch = fleet.Pitch; // defaults to the group's current orientation
                // orientation prefix: "flat ..." / "up ..." / "upright ..."
                string lowItem = item.ToLowerInvariant();
                if (lowItem.StartsWith("flat ")) { pitch = -90f; item = item.Substring(5).Trim(); }
                else if (lowItem.StartsWith("upright ")) { pitch = 0f; item = item.Substring(8).Trim(); }
                else if (lowItem.StartsWith("up ")) { pitch = 0f; item = item.Substring(3).Trim(); }

                string low = item.ToLowerInvariant();
                if (low == "line" || low == "grid" || low == "circle" || low == "sphere")
                {
                    Enum.TryParse(Capitalize(item), out FormationType ft);
                    parsed.Add((true, ft, null, 0f)); // formations use a horizontal base (pitch 0)
                }
                else
                {
                    List<Vector3> pts;
                    // "pattern <name>" / "pat <name>": display a saved pattern; otherwise treat as text
                    if (low.StartsWith("pattern ") || low.StartsWith("pat "))
                    {
                        string pname = item.Substring(item.IndexOf(' ') + 1).Trim();
                        if (_patternData == null || !_patternData.Patterns.TryGetValue(pname, out var prows))
                        { Msg(player, "Pattern_NotFound", pname); return; }
                        pts = Glyphs.LayoutRows(prows, config.TextDotSpacing);
                    }
                    else
                    {
                        pts = Glyphs.LayoutRows(Glyphs.TextToRows(item), config.TextDotSpacing);
                    }
                    parsed.Add((false, default(FormationType), pts, pitch));
                    maxNeeded = Mathf.Max(maxNeeded, pts.Count);
                }
            }
            if (maxNeeded == 0) maxNeeded = Mathf.Max(fleet.AliveAgents().Count, 16);
            if (maxNeeded > config.MaxDronesPerGroup)
            { Msg(player, "NeedExceedsMax", maxNeeded, config.MaxDronesPerGroup); return; }

            // each frame = (points, pitch)
            var frames = new List<(List<Vector3> pts, float pitch)>();
            foreach (var p in parsed)
            {
                if (p.isForm)
                {
                    var fpts = new List<Vector3>(maxNeeded);
                    for (int i = 0; i < maxNeeded; i++)
                        fpts.Add(FormationMath.Slot(p.ft, i, maxNeeded, config.DefaultSpacing));
                    frames.Add((fpts, 0f));
                }
                else frames.Add((p.pts, p.pitch));
            }

            AdjustFleetCount(fleet, maxNeeded);
            fleet.SetLights(true, config.LightPrefab);
            fleet.StartSequence(this, frames, interval);
            Msg(player, "Sequence_Started", items.Count, interval, maxNeeded, fleet.Name);
        }

        // ---- orient (text tilt) ----
        private void HandleOrient(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Orient"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            fleet.StopPresent(); // stop the auto showcase if running
            string mode = a[1].ToLower();
            if (mode == "upright") fleet.Pitch = 0f;
            else if (mode == "flat") fleet.Pitch = -90f; // orientation readable from below
            else if (mode == "tilt" && a.Length >= 3 && float.TryParse(a[2], out float deg)) fleet.Pitch = deg;
            else { Msg(player, "Orient_Modes"); return; }
            // displayed points apply Pitch instantly via WorldSlot, so drones tilt automatically
            Reply(player, Lang("Orient_Done", player, fleet.Name, fleet.Pitch) +
                (Mathf.Abs(fleet.Pitch) > 1f ? Lang("Orient_SpinHint", player) : ""));
        }

        // ---- spin (continuous rotation) ----
        private void HandleSpin(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Spin"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            if (a[1].ToLower() == "off") { fleet.StopSpin(); Msg(player, "Spin_Stopped", fleet.Name); return; }
            if (!float.TryParse(a[1], out float speed)) { Msg(player, "Spin_NeedNumber"); return; }
            fleet.StartSpin(this, speed);
            Msg(player, "Spin_Started", fleet.Name, speed);
        }

        // ---- present (auto showcase: loop through still poses) ----
        private void HandlePresent(BasePlayer player, string[] a)
        {
            if (a.Length < 1) { Msg(player, "Usage_Present"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            if (a.Length >= 2 && a[1].ToLower() == "off") { fleet.StopPresent(); Msg(player, "Present_Stopped", fleet.Name); return; }
            float hold = a.Length >= 2 && float.TryParse(a[1], out float hp) && hp >= 1f ? hp : 5f;
            fleet.StartPresent(this, hold);
            Msg(player, "Present_Started", fleet.Name, hold);
        }

        // grow/shrink the fleet's live drone count to match 'needed'
        private void AdjustFleetCount(DroneFleet fleet, int needed)
        {
            var alive = fleet.AliveAgents();
            int cur = alive.Count;
            if (cur < needed)
            {
                for (int i = cur; i < needed; i++)
                    AddShowDrone(fleet, i, fleet.Center + UnityEngine.Random.insideUnitSphere * 2f);
            }
            else
            {
                for (int i = cur - 1; i >= needed; i--)
                    if (alive[i] != null && alive[i].Drone != null && !alive[i].Drone.IsDestroyed)
                        alive[i].Drone.Kill();
            }
        }

        // ---- formation ----
        private void HandleFormation(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Formation"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            if (!Enum.TryParse(Capitalize(a[1]), out FormationType type))
            { Msg(player, "Formation_Types"); return; }
            float spacing = a.Length >= 3 && float.TryParse(a[2], out float s) ? s : config.DefaultSpacing;
            fleet.ApplyFormation(type, spacing);
            Msg(player, "Formation_Done", fleet.Name, type, spacing);
        }

        // ---- move ----
        private void HandleMove(BasePlayer player, string[] a)
        {
            if (a.Length < 1) { Msg(player, "Usage_Move"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            Vector3 target;
            if (a.Length >= 2 && a[1].ToLower() == "here")
                target = GetGroundPointInFront(player) + Vector3.up * config.DefaultSpawnHeight;
            else if (a.Length >= 4 && float.TryParse(a[1], out float x) && float.TryParse(a[2], out float y) && float.TryParse(a[3], out float z))
                target = new Vector3(x, y, z);
            else { Msg(player, "Usage_Move"); return; }
            fleet.Center = target;
            Msg(player, "Move_Done", fleet.Name);
        }

        // ---- rotate ----
        private void HandleRotate(BasePlayer player, string[] a)
        {
            if (a.Length < 2 || !float.TryParse(a[1], out float deg)) { Msg(player, "Usage_Rotate"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            fleet.Yaw = deg;
            Msg(player, "Rotate_Done", fleet.Name, deg);
        }

        // ---- scale ----
        private void HandleScale(BasePlayer player, string[] a)
        {
            if (a.Length < 2 || !float.TryParse(a[1], out float scale) || scale <= 0) { Msg(player, "Usage_Scale"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            fleet.Scale = scale;
            Msg(player, "Scale_Done", fleet.Name, scale);
        }

        // ---- light ----
        private void HandleLight(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Light"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            bool on = a[1].ToLower() == "on";
            fleet.SetLights(on, config.LightPrefab);
            Msg(player, "Light_Done", fleet.Name, on ? "ON" : "OFF");
        }

        // ---- show (simple auto choreography: cycle formations and spin) ----
        private void HandleShow(BasePlayer player, string[] a)
        {
            if (a.Length < 2) { Msg(player, "Usage_Show"); return; }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            bool on = a[1].ToLower() == "on";
            if (on)
            {
                fleet.SetLights(true, config.LightPrefab);
                fleet.StartShow(this);
                Msg(player, "Show_Started", fleet.Name);
            }
            else
            {
                fleet.StopShow();
                Msg(player, "Show_Stopped", fleet.Name);
            }
        }

        // ---- prefabs (search prefabs matching a keyword, across all prefab strings incl. effects) ----
        private void HandlePrefabs(BasePlayer player, string[] a)
        {
            string kw = a.Length >= 1 ? a[0].ToLower() : "drone";
            // pooledStrings holds all prefab paths incl. effects (entities only covers spawnable entities)
            var matches = GameManifest.Current.pooledStrings
                .Select(s => s.str)
                .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".prefab") && p.ToLower().Contains(kw))
                .Distinct()
                .OrderBy(p => p)
                .Take(40)
                .ToList();
            if (matches.Count == 0) { Msg(player, "Prefabs_None", kw); return; }
            var sb = new System.Text.StringBuilder(Lang("Prefabs_Header", player, kw, matches.Count));
            foreach (var m in matches) sb.AppendLine(m);
            Reply(player, sb.ToString());
            foreach (var m in matches) Puts(m);
        }

        // ---- list ----
        private void HandleList(BasePlayer player)
        {
            if (_fleets.Count == 0) { Msg(player, "List_Empty"); return; }
            var sb = new System.Text.StringBuilder(Lang("List_Header", player));
            foreach (var f in _fleets.Values)
                sb.AppendLine(Lang("List_Item", player, f.Name, f.Count, f.Center, f.Formation));
            Reply(player, sb.ToString());
        }

        // ---- clear ----
        private void HandleClear(BasePlayer player, string[] a)
        {
            if (a.Length < 1) { Msg(player, "Usage_Clear"); return; }
            if (a[0].ToLower() == "all")
            {
                foreach (var f in _fleets.Values.ToList()) f.DestroyAll();
                _fleets.Clear();
                Msg(player, "Clear_All");
                return;
            }
            var fleet = GetFleet(player, a[0]); if (fleet == null) return;
            fleet.DestroyAll();
            _fleets.Remove(fleet.Name);
            Msg(player, "Clear_Done", fleet.Name);
        }

        // =====================================================================
        //  Utilities
        // =====================================================================
        private DroneFleet GetFleet(BasePlayer player, string name)
        {
            if (_fleets.TryGetValue(name, out var f)) return f;
            Msg(player, "Group_NotFound", name);
            return null;
        }

        // create and return an empty group if missing (auto-created for text/pattern display; drones spawn via AdjustFleetCount)
        private DroneFleet GetOrCreateFleet(BasePlayer player, string name, out bool created)
        {
            created = false;
            if (_fleets.TryGetValue(name, out var f)) return f;
            var fleet = new DroneFleet(name, (ulong)player.userID)
            {
                Center = GetGroundPointInFront(player) + Vector3.up * config.DefaultSpawnHeight,
                Formation = FormationType.Grid,
                Spacing = config.DefaultSpacing
            };
            _fleets[name] = fleet;
            created = true;
            return fleet;
        }

        private bool HasPerm(BasePlayer player, string perm)
            => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, perm));

        private void Reply(BasePlayer player, string msg) => player.ChatMessage(msg);

        // =====================================================================
        //  Localization (uMod localization API)
        //  https://umod.org/documentation/api/localization
        //  English (en) is the default. Japanese (ja) is bundled too. Switches automatically by the player's language setting.
        // =====================================================================
        // Look up a key and return the message in the player's language (string.Format if args are given)
        public string Lang(string key, BasePlayer player = null, params object[] args)
        {
            string msg = lang.GetMessage(key, this, player?.UserIDString);
            return args != null && args.Length > 0 ? string.Format(msg, args) : msg;
        }

        // Send a localized message to a single player
        public void Msg(BasePlayer player, string key, params object[] args)
            => player.ChatMessage(Lang(key, player, args));

        // Broadcast to all players, each in their own language
        public void BroadcastLang(string key, params object[] args)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null) p.ChatMessage(Lang(key, p, args));
        }

        protected override void LoadDefaultMessages()
        {
            // ---- English (default) ----
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission.",
                ["Help_Drone"] =
                    "<color=#7FdBFF>DroneShow commands</color>\n" +
                    "/drone spawn <group> <count> [height] - create a group\n" +
                    "/drone formation <group> <line|grid|circle|sphere> [spacing] - formation\n" +
                    "/drone move <group> here|<x> <y> <z> - move the formation center\n" +
                    "/drone rotate <group> <deg> - formation heading (degrees)\n" +
                    "/drone scale <group> <factor> - scale the formation\n" +
                    "/drone light <group> <on|off> - lights\n" +
                    "/drone show <group> <on|off> - auto show (patrol + spin + lights)\n" +
                    "/drone text <group> <text> - display text (A-Z 0-9, drone count auto-adjusts)\n" +
                    "/drone pattern <group> <saved|rows..> - display an array/saved pattern\n" +
                    "/drone sequence <group> <sec> <item1>|<item2>.. - cycle text/formations\n" +
                    "/dronepattern - open the pattern editor (UI)\n" +
                    "/drone orient <group> <upright|flat> - text orientation (sideways/flat=readable from below)\n" +
                    "/drone spin <group> <speed|off> - continuous spin (show from all sides)\n" +
                    "/drone present <group> [hold sec|off] - auto showcase (pause facing each direction)\n" +
                    "/drone list - list groups\n" +
                    "/drone clear <group|all> - remove",
                ["Usage_Spawn"] = "Usage: /drone spawn <group> <count> [height]",
                ["Spawn_CountRange"] = "Count must be between 1 and {0}.",
                ["Group_Exists"] = "Group '{0}' already exists. Use /drone clear {0} to remove it.",
                ["Spawn_Done"] = "Spawned {1} drone(s) in group '{0}'.",
                ["Usage_Text"] = "Usage: /drone text <group> <text> (A-Z 0-9 and some symbols)",
                ["Group_Created"] = "Creating new group '{0}'.",
                ["Usage_Pattern"] = "Usage: /drone pattern <group> <saved name | row1 row2 ...>",
                ["NoPoints"] = "Nothing to display (supported: A-Z 0-9 and some symbols / patterns use # or 1 for lit cells).",
                ["NeedExceedsMaxHint"] = "Required drones {0} exceeds the limit {1}. Shorten the text or raise the limit (Max drones per group).",
                ["Text_Displayed"] = "Displaying {0} with {1} drones. Use /drone rotate {2} <deg> to orient and /drone scale {2} <factor> to resize.",
                ["Label_Text"] = "\"{0}\"",
                ["Label_PatternNamed"] = "pattern '{0}'",
                ["Label_Pattern"] = "pattern",
                ["Usage_Sequence"] = "Usage: /drone sequence <group> <switch sec> <item1> | <item2> ... (item = text, a shape line/grid/circle/sphere, or pattern <saved name>)  /  off to stop",
                ["Sequence_Stopped"] = "Stopped the sequence for '{0}'.",
                ["Usage_Sequence2"] = "Usage: /drone sequence <group> <switch sec (>=0.5)> <item1> | <item2> ...",
                ["Sequence_NeedItems"] = "Specify items separated by |. Example: flat HELLO | circle | pattern heart | up WORLD | sphere",
                ["NeedExceedsMax"] = "Required drones {0} exceeds the limit {1}.",
                ["Sequence_Started"] = "Cycling {0} item(s) every {1}s (up to {2} drones). Prefix items with flat/up to set orientation. /drone sequence {3} off to stop.",
                ["Usage_Orient"] = "Usage: /drone orient <group> <upright|flat|tilt <deg>>  (upright=sideways / flat=horizontal=readable from below)",
                ["Orient_Modes"] = "Orientation: upright (sideways=0°) / flat (horizontal=90°) / tilt <deg>",
                ["Orient_Done"] = "Set the tilt of '{0}' to {1}°.",
                ["Orient_SpinHint"] = " Use /drone spin to rotate and show from all sides.",
                ["Usage_Spin"] = "Usage: /drone spin <group> <deg/sec | off>",
                ["Spin_Stopped"] = "Stopped the spin of '{0}'.",
                ["Spin_NeedNumber"] = "Speed must be a number (deg/sec). Example: /drone spin g 30",
                ["Spin_Started"] = "Spinning '{0}' at {1} deg/sec. /drone spin {0} off to stop.",
                ["Usage_Present"] = "Usage: /drone present <group> [hold sec|off]",
                ["Present_Stopped"] = "Stopped the showcase of '{0}'.",
                ["Present_Started"] = "Auto-showcasing '{0}' (holding each pose for {1}s). Pauses facing down -> front -> right -> back -> left in a loop. /drone present {0} off to stop.",
                ["Usage_Formation"] = "Usage: /drone formation <group> <line|grid|circle|sphere> [spacing]",
                ["Formation_Types"] = "Formation types: line / grid / circle / sphere",
                ["Formation_Done"] = "Set '{0}' to a {1} formation ({2}m spacing).",
                ["Usage_Move"] = "Usage: /drone move <group> here | <x> <y> <z>",
                ["Move_Done"] = "Moved '{0}'.",
                ["Usage_Rotate"] = "Usage: /drone rotate <group> <deg>",
                ["Rotate_Done"] = "Set the heading of '{0}' to {1}°.",
                ["Usage_Scale"] = "Usage: /drone scale <group> <factor>",
                ["Scale_Done"] = "Set the scale of '{0}' to {1}x.",
                ["Usage_Light"] = "Usage: /drone light <group> <on|off>",
                ["Light_Done"] = "Set the lights of '{0}' to {1}.",
                ["Usage_Show"] = "Usage: /drone show <group> <on|off>",
                ["Show_Started"] = "Started the show for '{0}'. /drone show {0} off to stop.",
                ["Show_Stopped"] = "Stopped the show for '{0}'.",
                ["Prefabs_None"] = "No prefabs match '{0}'.",
                ["Prefabs_Header"] = "<color=#7FdBFF>'{0}' matches: {1} (full list in console)</color>\n",
                ["List_Empty"] = "There are no groups.",
                ["List_Header"] = "<color=#7FdBFF>Group list</color>\n",
                ["List_Item"] = "- {0}: {1} drone(s) / center {2} / {3}",
                ["Usage_Clear"] = "Usage: /drone clear <group|all>",
                ["Clear_All"] = "Removed all groups.",
                ["Clear_Done"] = "Removed '{0}'.",
                ["Group_NotFound"] = "Group '{0}' not found.",
                ["Game_Running"] = "A game is already running. /dronegame stop to end it.",
                ["Game_Started"] = "Wave game started! {0} waves total. Center: {1}",
                ["Boss_Spawned"] = "Spawned a boss at scale {0}.",
                ["Game_NotRunning"] = "No game is running.",
                ["Game_Stopped"] = "Ended the wave game.",
                ["Usage_GameFx"] = "Usage: /dronegame fx <effect prefab path>  (plays at your aim point to test)",
                ["Game_FxPlayed"] = "Playing effect: {0}\n(If nothing appears, that prefab is not rendered by Effect.server.Run.)",
                ["Help_Game"] =
                    "<color=#7FdBFF>DroneGame</color>\n" +
                    "/dronegame start [waves] - start (your aim point is the center)\n" +
                    "/dronegame boss [scale] - spawn one boss (for testing, size optional)\n" +
                    "/dronegame fx <prefab> - test-play an effect at your aim point\n" +
                    "/dronegame stop - end\n" +
                    "/dronegame status - status",
                ["Boss_Enraged"] = "<color=#FF3030>The boss is enraged! Billowing black smoke, it begins an all-out assault!</color>",
                ["Wave_BossAnnounce"] = "<color=#FF3030>★ Final wave {0}/{1} — boss incoming! ★</color>",
                ["Wave_Announce"] = "<color=#FF7F7F>Wave {0}/{1}</color> — {2} enemies (charge {3}/bomber {4}/gunner {5})",
                ["Game_Clear"] = "<color=#7FFF7F>All waves cleared! Victory!</color>",
                ["Score_Header"] = "<color=#7FdBFF>Kill ranking</color>",
                ["Score_Item"] = "- {0}: {1}",
                ["Status"] = "Wave {0}/{1} / {2} bomber(s) alive",
                ["Usage_PatternNew"] = "Usage: /dronepattern new <name> [width] [height]  (defaults to max size)",
                ["Pattern_SizeNumber"] = "Width and height must be numbers.",
                ["Pattern_SizeClamped"] = "Clamped to the UI editor limit of {0}×{0} (for larger art, edit data/DroneShow_Patterns.json directly).",
                ["Pattern_Created"] = "Created '{0}' ({1}x{2}). Click cells to edit, then save. /dronepattern link <group> for a live preview.",
                ["Usage_PatternEdit"] = "Usage: /dronepattern edit <name>",
                ["Pattern_NotFound"] = "Pattern '{0}' not found.",
                ["Pattern_Editing"] = "Editing '{0}'.",
                ["Usage_PatternLink"] = "Usage: /dronepattern link <group>",
                ["Pattern_OpenFirst"] = "Open one first with /dronepattern new or edit.",
                ["Pattern_GroupNotFound"] = "Group '{0}' not found. Create it with /drone spawn.",
                ["Pattern_Linked"] = "Live-previewing on group '{0}'. Edits apply instantly.",
                ["Pattern_ListEmpty"] = "There are no saved patterns.",
                ["Pattern_List"] = "Saved patterns: {0}",
                ["Usage_PatternDelete"] = "Usage: /dronepattern delete <name>",
                ["Pattern_Deleted"] = "Deleted '{0}'.",
                ["Pattern_DeleteNotFound"] = "Not found.",
                ["Pattern_Reloaded"] = "Reloaded {0} pattern(s) from data/DroneShow_Patterns.json.",
                ["Pattern_ReloadError"] = "Could not parse data/DroneShow_Patterns.json (see server console). Kept the current patterns.",
                ["Help_Pattern"] =
                    "<color=#7FdBFF>Pattern editor</color>\n" +
                    "/dronepattern new <name> <width> <height> - create new (UI editor)\n" +
                    "/dronepattern edit <name> - edit an existing one\n" +
                    "/dronepattern link <group> - live-preview edits on real drones\n" +
                    "/dronepattern list / delete <name>\n" +
                    "/dronepattern reload - reload patterns from the data file (after manual edits)\n" +
                    "After saving: /drone pattern <group> <name> to display",
                ["Pattern_Saved"] = "Saved pattern '{0}'. Display it with /drone pattern <group> {0}.",
                ["Ui_EditTitle"] = "Pattern edit: {0}  {1}x{2}   Pick a mode/brush and click cells (rectangle = click 2 points)",
                ["Ui_Save"] = "Save",
                ["Ui_Clear"] = "Clear",
                ["Ui_Close"] = "Close",
                ["Ui_ModePrefix"] = "Mode:",
                ["Ui_ModeDraw"] = "Draw",
                ["Ui_ModeErase"] = "Erase",
                ["Ui_ModeRect"] = "Rect",
                ["Ui_Brush"] = "Brush:{0}",
            }, this);

            // ---- Japanese (ja) ----
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "権限がありません。",
                ["Help_Drone"] =
                    "<color=#7FdBFF>DroneShow コマンド</color>\n" +
                    "/drone spawn <グループ名> <数> [高さ] - グループを生成\n" +
                    "/drone formation <グループ> <line|grid|circle|sphere> [間隔] - 編隊\n" +
                    "/drone move <グループ> here|<x> <y> <z> - 編隊中心を移動\n" +
                    "/drone rotate <グループ> <角度> - 編隊の向き(度)\n" +
                    "/drone scale <グループ> <倍率> - 編隊の拡縮\n" +
                    "/drone light <グループ> <on|off> - ライト\n" +
                    "/drone show <グループ> <on|off> - 自動ショー(編隊巡回+回転+ライト)\n" +
                    "/drone text <グループ> <文字> - 文字を表示(A-Z 0-9。機数は自動調整)\n" +
                    "/drone pattern <グループ> <保存名|行..> - 配列/保存パターンを表示\n" +
                    "/drone sequence <グループ> <秒> <項目1>|<項目2>.. - 文字/編隊を切替表示\n" +
                    "/dronepattern - パターン作成ツール(UIエディタ)を開く\n" +
                    "/drone orient <グループ> <upright|flat> - 文字の向き(横向き/水平=下から見れる)\n" +
                    "/drone spin <グループ> <速度|off> - 連続回転(全方向から見せる)\n" +
                    "/drone present <グループ> [保持秒|off] - 自動演出(止まって各方向から見せる)\n" +
                    "/drone list - グループ一覧\n" +
                    "/drone clear <グループ|all> - 撤去",
                ["Usage_Spawn"] = "使い方: /drone spawn <グループ名> <数> [高さ]",
                ["Spawn_CountRange"] = "数は 1〜{0} で指定してください。",
                ["Group_Exists"] = "グループ '{0}' は既に存在します。/drone clear {0} で撤去してください。",
                ["Spawn_Done"] = "グループ '{0}' に {1} 機を生成しました。",
                ["Usage_Text"] = "使い方: /drone text <グループ> <文字> (A-Z 0-9 一部記号)",
                ["Group_Created"] = "グループ '{0}' を新規作成します。",
                ["Usage_Pattern"] = "使い方: /drone pattern <グループ> <保存名 | 行1 行2 ...>",
                ["NoPoints"] = "表示できる点がありません(対応文字: A-Z 0-9 一部記号 / パターンは # か 1 で点灯)。",
                ["NeedExceedsMaxHint"] = "必要機数 {0} が上限 {1} を超えます。文字数を減らすか上限(Max drones per group)を上げてください。",
                ["Text_Displayed"] = "{0} を {1} 機で表示しました。/drone rotate {2} <角度> で向き、/drone scale {2} <倍率> で大きさを調整できます。",
                ["Label_Text"] = "「{0}」",
                ["Label_PatternNamed"] = "パターン '{0}'",
                ["Label_Pattern"] = "パターン",
                ["Usage_Sequence"] = "使い方: /drone sequence <グループ> <切替秒> <項目1> | <項目2> ... (項目=文字 / 形 line/grid/circle/sphere / pattern <保存名>)  /  off で停止",
                ["Sequence_Stopped"] = "'{0}' のシーケンスを停止しました。",
                ["Usage_Sequence2"] = "使い方: /drone sequence <グループ> <切替秒(0.5以上)> <項目1> | <項目2> ...",
                ["Sequence_NeedItems"] = "項目を | 区切りで指定してください。例: flat HELLO | circle | pattern heart | up WORLD | sphere",
                ["NeedExceedsMax"] = "必要機数 {0} が上限 {1} を超えます。",
                ["Sequence_Started"] = "{0} 項目を {1} 秒ごとに切替表示します（最大 {2} 機）。各項目の頭に flat/up を付けると向き指定可。/drone sequence {3} off で停止。",
                ["Usage_Orient"] = "使い方: /drone orient <グループ> <upright|flat|tilt <度>>  (upright=横向き / flat=水平=下から見れる)",
                ["Orient_Modes"] = "向き: upright(横向き=0°) / flat(水平=90°) / tilt <度>",
                ["Orient_Done"] = "'{0}' の傾きを {1}° にしました。",
                ["Orient_SpinHint"] = " /drone spin で回すと全方向から見れます。",
                ["Usage_Spin"] = "使い方: /drone spin <グループ> <度/秒 | off>",
                ["Spin_Stopped"] = "'{0}' の回転を停止しました。",
                ["Spin_NeedNumber"] = "速度は数値(度/秒)で指定してください。例: /drone spin g 30",
                ["Spin_Started"] = "'{0}' を {1} 度/秒 で回転させます。/drone spin {0} off で停止。",
                ["Usage_Present"] = "使い方: /drone present <グループ> [保持秒|off]",
                ["Present_Stopped"] = "'{0}' の自動演出を停止しました。",
                ["Present_Started"] = "'{0}' を自動演出（各ポーズ {1}秒 保持）。下向き→正面→右→裏→左 を順に静止して見せ、ループします。/drone present {0} off で停止。",
                ["Usage_Formation"] = "使い方: /drone formation <グループ> <line|grid|circle|sphere> [間隔]",
                ["Formation_Types"] = "編隊種別: line / grid / circle / sphere",
                ["Formation_Done"] = "'{0}' を {1} 編隊({2}m間隔)にしました。",
                ["Usage_Move"] = "使い方: /drone move <グループ> here | <x> <y> <z>",
                ["Move_Done"] = "'{0}' を移動させました。",
                ["Usage_Rotate"] = "使い方: /drone rotate <グループ> <角度>",
                ["Rotate_Done"] = "'{0}' の向きを {1}° にしました。",
                ["Usage_Scale"] = "使い方: /drone scale <グループ> <倍率>",
                ["Scale_Done"] = "'{0}' のスケールを {1}x にしました。",
                ["Usage_Light"] = "使い方: /drone light <グループ> <on|off>",
                ["Light_Done"] = "'{0}' のライトを {1} にしました。",
                ["Usage_Show"] = "使い方: /drone show <グループ> <on|off>",
                ["Show_Started"] = "'{0}' のショーを開始しました。/drone show {0} off で停止。",
                ["Show_Stopped"] = "'{0}' のショーを停止しました。",
                ["Prefabs_None"] = "'{0}' に合致するプレハブはありません。",
                ["Prefabs_Header"] = "<color=#7FdBFF>'{0}' 合致 {1}件（コンソールに全文）</color>\n",
                ["List_Empty"] = "グループはありません。",
                ["List_Header"] = "<color=#7FdBFF>グループ一覧</color>\n",
                ["List_Item"] = "・{0}: {1}機 / 中心 {2} / {3}",
                ["Usage_Clear"] = "使い方: /drone clear <グループ|all>",
                ["Clear_All"] = "全グループを撤去しました。",
                ["Clear_Done"] = "'{0}' を撤去しました。",
                ["Group_NotFound"] = "グループ '{0}' が見つかりません。",
                ["Game_Running"] = "既にゲーム進行中です。/dronegame stop で終了。",
                ["Game_Started"] = "ウェーブゲーム開始! 全{0}ウェーブ。中心: {1}",
                ["Boss_Spawned"] = "ボスを倍率 {0} で出現させました。",
                ["Game_NotRunning"] = "進行中のゲームはありません。",
                ["Game_Stopped"] = "ウェーブゲームを終了しました。",
                ["Usage_GameFx"] = "使い方: /dronegame fx <エフェクトのプレハブパス>  (視線の先で再生してテスト)",
                ["Game_FxPlayed"] = "エフェクト再生: {0}\n（何も見えなければ、そのプレハブは Effect.server.Run で描画されません）",
                ["Help_Game"] =
                    "<color=#7FdBFF>DroneGame</color>\n" +
                    "/dronegame start [ウェーブ数] - 開始(視線先が中心)\n" +
                    "/dronegame boss [倍率] - ボスを1体出す(テスト用・サイズ指定可)\n" +
                    "/dronegame fx <プレハブ> - エフェクトを視線先で再生テスト\n" +
                    "/dronegame stop - 終了\n" +
                    "/dronegame status - 状況",
                ["Boss_Enraged"] = "<color=#FF3030>ボスが激昂した! 黒煙を上げ猛攻を開始!</color>",
                ["Wave_BossAnnounce"] = "<color=#FF3030>★ 最終ウェーブ {0}/{1} — ボス出現! ★</color>",
                ["Wave_Announce"] = "<color=#FF7F7F>ウェーブ {0}/{1}</color> — 敵 {2} 機 (突撃{3}/爆撃{4}/射撃{5})",
                ["Game_Clear"] = "<color=#7FFF7F>全ウェーブ突破! クリア!</color>",
                ["Score_Header"] = "<color=#7FdBFF>撃墜ランキング</color>",
                ["Score_Item"] = "・{0}: {1}",
                ["Status"] = "ウェーブ {0}/{1} / 生存爆撃機 {2}機",
                ["Usage_PatternNew"] = "使い方: /dronepattern new <名前> [幅] [高さ]  (省略時は最大サイズ)",
                ["Pattern_SizeNumber"] = "幅・高さは数値で指定してください。",
                ["Pattern_SizeClamped"] = "UIエディタの上限 {0}×{0} に丸めました（さらに大きい絵は data/DroneShow_Patterns.json を直接編集）。",
                ["Pattern_Created"] = "'{0}' を新規作成({1}x{2})。セルをクリックして編集→保存。/dronepattern link <グループ> で実機プレビュー。",
                ["Usage_PatternEdit"] = "使い方: /dronepattern edit <名前>",
                ["Pattern_NotFound"] = "パターン '{0}' がありません。",
                ["Pattern_Editing"] = "'{0}' を編集中。",
                ["Usage_PatternLink"] = "使い方: /dronepattern link <グループ>",
                ["Pattern_OpenFirst"] = "先に /dronepattern new か edit で開いてください。",
                ["Pattern_GroupNotFound"] = "グループ '{0}' がありません。/drone spawn で作成してください。",
                ["Pattern_Linked"] = "グループ '{0}' に実機プレビューします。編集が即反映されます。",
                ["Pattern_ListEmpty"] = "保存パターンはありません。",
                ["Pattern_List"] = "保存パターン: {0}",
                ["Usage_PatternDelete"] = "使い方: /dronepattern delete <名前>",
                ["Pattern_Deleted"] = "'{0}' を削除しました。",
                ["Pattern_DeleteNotFound"] = "見つかりません。",
                ["Pattern_Reloaded"] = "data/DroneShow_Patterns.json から {0} 件のパターンを再読込しました。",
                ["Pattern_ReloadError"] = "data/DroneShow_Patterns.json を解析できませんでした(サーバーコンソール参照)。現在のパターンを維持します。",
                ["Help_Pattern"] =
                    "<color=#7FdBFF>パターン作成ツール</color>\n" +
                    "/dronepattern new <名前> <幅> <高さ> - 新規作成(UIエディタ)\n" +
                    "/dronepattern edit <名前> - 既存を編集\n" +
                    "/dronepattern link <グループ> - 編集を実機に即プレビュー\n" +
                    "/dronepattern list / delete <名前>\n" +
                    "/dronepattern reload - データファイルからパターンを再読込(手編集後に)\n" +
                    "保存後: /drone pattern <グループ> <名前> で表示",
                ["Pattern_Saved"] = "パターン '{0}' を保存しました。/drone pattern <グループ> {0} で表示できます。",
                ["Ui_EditTitle"] = "パターン編集: {0}  {1}x{2}   モード/筆を選んでセルをクリック（矩形は2点クリック）",
                ["Ui_Save"] = "保存",
                ["Ui_Clear"] = "クリア",
                ["Ui_Close"] = "閉じる",
                ["Ui_ModePrefix"] = "モード:",
                ["Ui_ModeDraw"] = "描画",
                ["Ui_ModeErase"] = "消去",
                ["Ui_ModeRect"] = "矩形",
                ["Ui_Brush"] = "筆:{0}",
            }, this, "ja");
        }

        // Return the ground/forward point in the player's look direction (spawn reference)
        private Vector3 GetGroundPointInFront(BasePlayer player)
        {
            Ray ray = new Ray(player.eyes.position, player.eyes.HeadForward());
            if (Physics.Raycast(ray, out RaycastHit hit, 50f, Rust.Layers.Solid))
                return hit.point;
            return player.eyes.position + player.eyes.HeadForward() * 10f;
        }

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1).ToLower();

        // =====================================================================
        //  Fleet (group of drones for formations/shows)
        // =====================================================================
        public class DroneFleet
        {
            public string Name;
            public ulong OwnerId;
            public readonly List<DroneAgent> Agents = new List<DroneAgent>();

            public Vector3 Center;
            public float Yaw;           // degrees
            public float Scale = 1f;
            public FormationType Formation = FormationType.Grid;
            public float Spacing = 2f;
            public float Pitch;               // text tilt (deg) 0=sideways / 90=horizontal (readable from below)

            public int Count => Agents.Count(a => a != null && a.Drone != null && !a.Drone.IsDestroyed);

            public DroneFleet(string name, ulong ownerId) { Name = name; OwnerId = ownerId; }

            public void Add(DroneAgent agent) => Agents.Add(agent);

            public void ApplyFormation(FormationType type, float spacing)
            {
                StopPresent();
                Pitch = 0f; // formations reset to the horizontal base
                Formation = type;
                Spacing = spacing;
                RecalculateSlots();
            }

            // assign each agent a local slot position (relative to the formation center)
            public void RecalculateSlots()
            {
                var alive = Agents.Where(a => a != null && a.Drone != null && !a.Drone.IsDestroyed).ToList();
                int n = alive.Count;
                for (int i = 0; i < n; i++)
                {
                    alive[i].LocalSlot = FormationMath.Slot(Formation, i, n, Spacing);
                    alive[i].Parked = false;
                }
            }

            // world coordinate reflecting formation center, tilt (Pitch), heading (Yaw), and scale
            public Vector3 WorldSlot(Vector3 local)
            {
                var rot = Quaternion.Euler(Pitch, Yaw, 0f);
                return Center + rot * (local * Scale);
            }

            public List<DroneAgent> AliveAgents()
                => Agents.Where(a => a != null && a.Drone != null && !a.Drone.IsDestroyed).ToList();

            // assign the points (local coords) to each drone. Surplus drones are Parked and hidden.
            public void ApplyPoints(List<Vector3> points)
            {
                var alive = AliveAgents();
                for (int i = 0; i < alive.Count; i++)
                {
                    if (i < points.Count)
                    {
                        alive[i].LocalSlot = points[i];
                        alive[i].Parked = false;
                    }
                    else alive[i].Parked = true;
                }
            }

            // --- sequence (frame-by-frame switching of text/formations) ---
            private Timer _seqTimer;
            private List<(List<Vector3> pts, float pitch)> _seqFrames;
            private int _seqIndex;

            public void StartSequence(DroneShow plugin, List<(List<Vector3> pts, float pitch)> frames, float interval)
            {
                StopShow();
                if (frames == null || frames.Count == 0) return;
                _seqFrames = frames;
                _seqIndex = 0;
                ShowFrame(0);
                _seqTimer = plugin.Every(interval, () =>
                {
                    if (_seqFrames == null || _seqFrames.Count == 0) return;
                    _seqIndex = (_seqIndex + 1) % _seqFrames.Count;
                    ShowFrame(_seqIndex);
                });
            }

            private void ShowFrame(int i)
            {
                Pitch = _seqFrames[i].pitch;
                ApplyPoints(_seqFrames[i].pts);
                RefreshParkedLights(); // turn off lights on surplus drones
            }

            public void StopSequence()
            {
                _seqTimer?.Destroy();
                _seqTimer = null;
                _seqFrames = null;
            }

            // --- continuous spin (e.g. show horizontal text from all sides) ---
            private Timer _spinTimer;
            public void StartSpin(DroneShow plugin, float degPerSec)
            {
                StopSpin();
                _spinTimer = plugin.Every(0.1f, () => { Yaw += degPerSec * 0.1f; });
            }
            public void StopSpin()
            {
                _spinTimer?.Destroy();
                _spinTimer = null;
            }

            // --- auto showcase: hold a series of "still display poses" in order and loop ---
            // pause at each pose -> gives drones time to align so the text looks clean.
            private Timer _presentTimer;
            private int _presentPose;
            private static readonly (float pitch, float yaw)[] PresentPoses =
            {
                (-90f, 0f),   // horizontal (readable correctly from directly below)
                (0f, 0f),     // sideways - front
                (0f, 90f),    // sideways - right
                (0f, 180f),   // sideways - back
                (0f, 270f),   // sideways - left
            };
            public void StartPresent(DroneShow plugin, float holdTime)
            {
                StopSpin();
                StopPresent();
                _presentPose = 0;
                Pitch = PresentPoses[0].pitch; Yaw = PresentPoses[0].yaw;
                _presentTimer = plugin.Every(holdTime, () =>
                {
                    _presentPose = (_presentPose + 1) % PresentPoses.Length;
                    Pitch = PresentPoses[_presentPose].pitch;
                    Yaw = PresentPoses[_presentPose].yaw;
                });
            }
            public void StopPresent()
            {
                _presentTimer?.Destroy();
                _presentTimer = null;
            }

            public void SetLights(bool on, string prefab)
            {
                // guard against Unity's "fake null": ?. slips past destroyed objects, so check explicitly
                foreach (var a in Agents)
                {
                    if (a == null || a.Drone == null || a.Drone.IsDestroyed) continue;
                    a.SetLight(on, prefab);
                }
            }

            // turn off lights on surplus (Parked) drones, light up the ones in use
            public void RefreshParkedLights()
            {
                foreach (var a in AliveAgents())
                    a.SetLightActive(!a.Parked);
            }

            // --- simple show (cycle formations + continuous spin) ---
            private Timer _showFormationTimer;
            private Timer _showSpinTimer;
            private int _showStep;
            private static readonly FormationType[] ShowCycle =
                { FormationType.Sphere, FormationType.Circle, FormationType.Grid, FormationType.Line };

            public void StartShow(DroneShow plugin)
            {
                StopShow();
                _showStep = 0;
                // switch formation every 5 seconds
                _showFormationTimer = plugin.Every(5f, () =>
                {
                    var type = ShowCycle[_showStep % ShowCycle.Length];
                    ApplyFormation(type, Spacing);
                    _showStep++;
                });
                // rotate a little every 0.1 seconds
                _showSpinTimer = plugin.Every(0.1f, () => { Yaw += 3f; });
            }

            public void StopShow()
            {
                _showFormationTimer?.Destroy();
                _showSpinTimer?.Destroy();
                _showFormationTimer = null;
                _showSpinTimer = null;
                StopSequence();
            }

            public void DestroyAll()
            {
                StopShow();
                StopSpin();
                StopPresent();
                foreach (var a in Agents.ToList())
                {
                    if (a != null && a.Drone != null && !a.Drone.IsDestroyed)
                        a.Drone.Kill();
                }
                Agents.Clear();
            }
        }

        // =====================================================================
        //  Formation math
        // =====================================================================
        public enum FormationType { Line, Grid, Circle, Sphere }

        public static class FormationMath
        {
            // from index i / total n / spacing, return the center-relative local coordinate
            public static Vector3 Slot(FormationType type, int i, int n, float spacing)
            {
                switch (type)
                {
                    case FormationType.Line:
                        {
                            float offset = (n - 1) * 0.5f;
                            return new Vector3((i - offset) * spacing, 0f, 0f);
                        }
                    case FormationType.Grid:
                        {
                            int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
                            int rows = Mathf.CeilToInt((float)n / cols);
                            int cx = i % cols;
                            int cz = i / cols;
                            float ox = (cols - 1) * 0.5f;
                            float oz = (rows - 1) * 0.5f;
                            return new Vector3((cx - ox) * spacing, 0f, (cz - oz) * spacing);
                        }
                    case FormationType.Circle:
                        {
                            float radius = Mathf.Max(spacing, n * spacing / (2f * Mathf.PI));
                            float ang = (2f * Mathf.PI / Mathf.Max(1, n)) * i;
                            return new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
                        }
                    case FormationType.Sphere:
                        {
                            // even distribution via a Fibonacci sphere
                            float radius = Mathf.Max(spacing, n * spacing / (4f * Mathf.PI));
                            float k = i + 0.5f;
                            float phi = Mathf.Acos(1f - 2f * k / Mathf.Max(1, n));
                            float theta = Mathf.PI * (1f + Mathf.Sqrt(5f)) * k;
                            return new Vector3(
                                Mathf.Cos(theta) * Mathf.Sin(phi),
                                Mathf.Cos(phi),
                                Mathf.Sin(theta) * Mathf.Sin(phi)) * radius;
                        }
                }
                return Vector3.zero;
            }
        }

        // =====================================================================
        //  Text / pattern display (dot matrix)
        // =====================================================================
        public static class Glyphs
        {
            // '#'=lit '.'=off. 5x7. Returns center-relative local points (upright: x right / y up / z=0).
            // Tilt (sideways/horizontal) is applied on the display side via Pitch (WorldSlot).
            public static List<Vector3> LayoutRows(IReadOnlyList<string> rows, float spacing)
            {
                var pts = new List<Vector3>();
                int height = rows.Count;
                int width = 0;
                foreach (var r in rows) width = Mathf.Max(width, r.Length);
                if (height == 0 || width == 0) return pts;

                for (int r = 0; r < height; r++)
                {
                    string row = rows[r];
                    for (int c = 0; c < row.Length; c++)
                    {
                        char ch = row[c];
                        if (ch == '#' || ch == '1' || ch == '*')
                        {
                            float x = (c - (width - 1) * 0.5f) * spacing;
                            float v = ((height - 1) * 0.5f - r) * spacing;
                            pts.Add(new Vector3(x, v, 0f));
                        }
                    }
                }
                return pts;
            }

            // expand a string into 7 rows using the 5x7 font (can be passed to LayoutRows)
            public static List<string> TextToRows(string message)
            {
                var rows = new List<string>(7);
                for (int i = 0; i < 7; i++) rows.Add("");
                bool first = true;
                foreach (char raw in message.ToUpperInvariant())
                {
                    if (!Font.TryGetValue(raw, out var g))
                    {
                        if (raw == ' ') g = Font[' '];
                        else continue; // skip unsupported characters
                    }
                    if (!first)
                        for (int r = 0; r < 7; r++) rows[r] += "."; // gap between characters
                    first = false;
                    for (int r = 0; r < 7; r++) rows[r] += g[r];
                }
                return rows;
            }

            // 5x7 font
            private static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]>
            {
                [' '] = new[]{ ".....", ".....", ".....", ".....", ".....", ".....", "....." },
                ['A'] = new[]{ ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
                ['B'] = new[]{ "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
                ['C'] = new[]{ ".####", "#....", "#....", "#....", "#....", "#....", ".####" },
                ['D'] = new[]{ "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
                ['E'] = new[]{ "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
                ['F'] = new[]{ "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
                ['G'] = new[]{ ".####", "#....", "#....", "#..##", "#...#", "#...#", ".####" },
                ['H'] = new[]{ "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
                ['I'] = new[]{ "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" },
                ['J'] = new[]{ "..###", "...#.", "...#.", "...#.", "#..#.", "#..#.", ".##.." },
                ['K'] = new[]{ "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
                ['L'] = new[]{ "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
                ['M'] = new[]{ "#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#", "#...#" },
                ['N'] = new[]{ "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#" },
                ['O'] = new[]{ ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
                ['P'] = new[]{ "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
                ['Q'] = new[]{ ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" },
                ['R'] = new[]{ "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
                ['S'] = new[]{ ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
                ['T'] = new[]{ "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
                ['U'] = new[]{ "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
                ['V'] = new[]{ "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." },
                ['W'] = new[]{ "#...#", "#...#", "#...#", "#...#", "#.#.#", "##.##", "#...#" },
                ['X'] = new[]{ "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" },
                ['Y'] = new[]{ "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." },
                ['Z'] = new[]{ "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
                ['0'] = new[]{ ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
                ['1'] = new[]{ "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
                ['2'] = new[]{ ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
                ['3'] = new[]{ "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
                ['4'] = new[]{ "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
                ['5'] = new[]{ "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
                ['6'] = new[]{ ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###." },
                ['7'] = new[]{ "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
                ['8'] = new[]{ ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
                ['9'] = new[]{ ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###." },
                ['!'] = new[]{ "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." },
                ['?'] = new[]{ ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.." },
                ['.'] = new[]{ ".....", ".....", ".....", ".....", ".....", ".....", "..#.." },
                ['-'] = new[]{ ".....", ".....", ".....", "#####", ".....", ".....", "....." },
                ['+'] = new[]{ ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
                ['<'] = new[]{ "...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#." },
                ['>'] = new[]{ ".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..." },
            };
        }

        // =====================================================================
        //  Drone agent (component attached to each drone)
        // =====================================================================
        public enum AgentMode { Formation, Enemy }
        public enum EnemyType { Charger, Bomber, GunnerRapid, GunnerSniper, GunnerShotgun, Boss }

        public class DroneAgent : MonoBehaviour
        {
            public Drone Drone { get; private set; }
            public DroneFleet Fleet { get; private set; }
            public int Index;
            public Vector3 LocalSlot;
            public bool Invulnerable;
            public bool Parked;        // hide surplus drones (unused by the text/pattern)
            public AgentMode Mode = AgentMode.Formation;

            // --- enemy mode ---
            public WaveGame Game;
            public EnemyType Enemy;
            private float _lastAttackTime;   // gun/bomb/charge
            private float _lastRocketTime;   // boss rocket
            private float _lastSmokeTime;    // boss smoke
            private float _lastFireDrop;     // boss fireball drop
            private BaseEntity _fire;        // flame entity for the boss burning
            private float _orbitAngle;       // radians
            private bool _enraged;           // boss: low-health phase
            private BasePlayer _target;

            private DroneShow _plugin;
            private BaseEntity _light;

            public void Init(DroneShow plugin, DroneFleet fleet, int index)
            {
                _plugin = plugin;
                Fleet = fleet;
                Index = index;
                Drone = GetComponent<Drone>();
                LocalSlot = FormationMath.Slot(fleet.Formation, index, Mathf.Max(1, fleet.Agents.Count), fleet.Spacing);

                // prevent instant death from water/terrain during a show
                if (Drone != null)
                {
                    Drone.killInWater = false;
                    Drone.killInTerrain = false;
                }
                _plugin.RegisterAgent(this);
            }

            // initialize as an enemy drone
            public void InitEnemy(DroneShow plugin, WaveGame game, EnemyType type, int index, int total)
            {
                _plugin = plugin;
                Game = game;
                Enemy = type;
                Mode = AgentMode.Enemy;
                Invulnerable = false;
                Index = index;
                Drone = GetComponent<Drone>();
                var cfg = plugin.CfgPublic;
                float hp = cfg.HealthFor(type);
                Drone.InitializeHealth(hp, hp);
                Drone.killInWater = false;
                Drone.killInTerrain = false;
                // give each drone a different starting orbit angle so they circle as a group
                _orbitAngle = (2f * Mathf.PI / Mathf.Max(1, total)) * index;
                _lastAttackTime = Time.time;
                _lastRocketTime = Time.time;
                _plugin.RegisterAgent(this);
            }

            // called every tick from the plugin timer
            public void ServerTick()
            {
                if (Drone == null || Drone.IsDestroyed) return;
                LockLightDown();
                if (Mode == AgentMode.Enemy) { EnemyTick(); return; }
                if (Fleet == null) return;

                // physics nav: set targetPosition and let Drone.Update_Server follow it
                // Parked surplus drones retreat below the world (underground) to hide, regardless of heading
                Drone.targetPosition = Parked
                    ? Fleet.Center + Vector3.down * 60f
                    : Fleet.WorldSlot(LocalSlot);
            }

            private void EnemyTick()
            {
                if (Game == null || !Game.Running) return;
                var cfg = _plugin.CfgPublic;
                _target = Game.FindNearestTarget(Drone.transform.position);
                if (_target == null)
                {
                    Drone.targetPosition = Game.Center + Vector3.up * cfg.AttackHeight;
                    return;
                }
                Vector3 tpos = _target.transform.position;

                switch (Enemy)
                {
                    case EnemyType.Charger:
                        // charge straight at the player and self-destruct when close
                        Drone.targetPosition = tpos + Vector3.up * 0.5f;
                        if (Vector3.Distance(Drone.transform.position, tpos) <= cfg.ChargerExplodeRadius)
                            _plugin.DetonateCharger(Drone, Game);
                        break;

                    case EnemyType.Bomber:
                        OrbitAround(tpos, cfg.OrbitRadius, cfg.AttackHeight, cfg.OrbitSpeed);
                        if (Time.time - _lastAttackTime >= cfg.BombInterval)
                        {
                            _lastAttackTime = Time.time;
                            _plugin.DropBomb(Drone, tpos); // throw it toward the player
                        }
                        break;

                    case EnemyType.GunnerRapid: // rapid-fire: close range, fast bursts, low damage
                        OrbitAround(tpos, cfg.OrbitRadius * 1.2f, cfg.AttackHeight * 0.7f, cfg.OrbitSpeed * 1.2f);
                        if (Time.time - _lastAttackTime >= cfg.RapidInterval)
                        {
                            _lastAttackTime = Time.time;
                            _plugin.EnemyShoot(Drone, _target, cfg.RapidDamage, cfg.RapidSpread, 1);
                        }
                        break;

                    case EnemyType.GunnerSniper: // sniper: fast, high-damage, high-accuracy single shots
                        OrbitAround(tpos, cfg.OrbitRadius * 2.2f, cfg.AttackHeight * 1.3f, cfg.OrbitSpeed * 0.5f);
                        if (Time.time - _lastAttackTime >= cfg.SniperInterval)
                        {
                            _lastAttackTime = Time.time;
                            _plugin.EnemyShoot(Drone, _target, cfg.SniperDamage, cfg.SniperSpread, 1);
                        }
                        break;

                    case EnemyType.GunnerShotgun: // shotgun: fires a spread of pellets at close range
                        OrbitAround(tpos, cfg.OrbitRadius * 0.8f, cfg.AttackHeight * 0.6f, cfg.OrbitSpeed);
                        if (Time.time - _lastAttackTime >= cfg.ShotgunInterval)
                        {
                            _lastAttackTime = Time.time;
                            _plugin.EnemyShoot(Drone, _target, cfg.ShotgunDamage, cfg.ShotgunSpread, cfg.ShotgunPellets);
                        }
                        break;

                    case EnemyType.Boss:
                        BossTick(tpos, cfg);
                        break;
                }
            }

            // Boss: uses guns constantly; rockets are normally rare / frequent at low health. Black smoke at low health.
            private void BossTick(Vector3 tpos, Configuration cfg)
            {
                OrbitAround(tpos, cfg.BossOrbitRadius, cfg.AttackHeight * 1.4f, cfg.OrbitSpeed * 0.6f);

                // drop fireballs scattered on the ground (only when enraged. Impact points burn = dodge gameplay. Deals damage)
                if (_enraged && !string.IsNullOrEmpty(cfg.FireDropPrefab) && Time.time - _lastFireDrop >= cfg.FireDropInterval)
                {
                    _lastFireDrop = Time.time;
                    Vector3 from = Drone.transform.position + Vector3.down * 1f;
                    var fb = GameManager.server.CreateEntity(cfg.FireDropPrefab, from);
                    if (fb != null)
                    {
                        fb.creatorEntity = null;
                        fb.Spawn();
                        // scatter with a downward velocity plus a little random horizontal jitter
                        Vector3 vel = Vector3.down * cfg.FireDropSpeed + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-3f, 3f));
                        fb.SetVelocity(vel);
                    }
                }

                // health phase check
                float maxHp = Drone.MaxHealth();
                if (!_enraged && maxHp > 0f && Drone.health / maxHp <= cfg.BossEnrageHealthPct)
                {
                    _enraged = true;
                    DroneShow.Instance.BroadcastLang("Boss_Enraged");
                }

                // gun (constant)
                if (Time.time - _lastAttackTime >= cfg.BossGunInterval)
                {
                    _lastAttackTime = Time.time;
                    _plugin.EnemyShoot(Drone, _target, cfg.BossGunDamage, cfg.BossGunSpread, cfg.BossGunPellets);
                }

                // rocket (normally rare / frequent when enraged)
                float rocketInterval = _enraged ? cfg.RocketIntervalEnraged : cfg.RocketInterval;
                if (Time.time - _lastRocketTime >= rocketInterval)
                {
                    _lastRocketTime = Time.time;
                    _plugin.FireRocket(Drone, _target);
                }

                // set the boss on fire while enraged (flame + smoke visuals. Respawn if it disappears)
                if (_enraged && !string.IsNullOrEmpty(cfg.BossFirePrefab) &&
                    (_fire == null || _fire.IsDestroyed))
                {
                    var fb = GameManager.server.CreateEntity(cfg.BossFirePrefab, Drone.transform.position);
                    if (fb != null)
                    {
                        if (fb is FireBall fball) fball.damagePerSecond = 0f; // make it harmless (visual only)
                        var rb = fb.GetComponent<Rigidbody>();
                        if (rb != null) rb.isKinematic = true;       // keep it from falling
                        var col = fb.GetComponent<Collider>();
                        if (col != null) col.enabled = false;
                        fb.Spawn();
                        fb.SetParent(Drone, worldPositionStays: true);
                        fb.transform.localPosition = Vector3.zero;
                        _fire = fb;
                    }
                }

                // keep emitting black smoke while enraged (optional; disabled by default via empty path)
                if (_enraged && !string.IsNullOrEmpty(cfg.BossSmokeEffect) &&
                    Time.time - _lastSmokeTime >= cfg.BossSmokeInterval)
                {
                    _lastSmokeTime = Time.time;
                    Effect.server.Run(cfg.BossSmokeEffect, Drone.transform.position, Vector3.up, null, true);
                }
            }

            // orbit the target as a group (so they don't all clump in one spot)
            private void OrbitAround(Vector3 center, float radius, float height, float speed)
            {
                _orbitAngle += speed * 0.1f; // tick=0.1s
                Vector3 offset = new Vector3(Mathf.Cos(_orbitAngle), 0f, Mathf.Sin(_orbitAngle)) * radius;
                Drone.targetPosition = center + offset + Vector3.up * height;
            }

            public void SetLight(bool on, string prefab)
            {
                if (Drone == null || Drone.IsDestroyed) return;
                if (on)
                {
                    if (_light != null && !_light.IsDestroyed) return;
                    if (string.IsNullOrEmpty(prefab)) return;
                    // spawn at the body center as a fixed beam always pointing straight down
                    var ent = GameManager.server.CreateEntity(prefab, Drone.transform.position, Quaternion.LookRotation(Vector3.down));
                    if (ent == null) return;
                    ent.Spawn();
                    ent.SetParent(Drone, true); // parent while keeping world orientation (straight down)
                    ent.transform.localPosition = Vector3.zero;
                    ForcePowerOn(ent);
                    _light = ent;
                    _lightActive = true;
                }
                else
                {
                    if (_light != null && !_light.IsDestroyed) _light.Kill();
                    _light = null;
                }
            }

            // pin the light straight down (world) every tick. It always shines down even if the drone tilts.
            private void LockLightDown()
            {
                if (_light == null || _light.IsDestroyed) return;
                Quaternion down = Quaternion.LookRotation(Vector3.down);
                if (Quaternion.Angle(_light.transform.rotation, down) < 0.5f) return; // skip update if the change is tiny
                _light.transform.rotation = down;
                _light.SendNetworkUpdate();
            }

            // light up the IO light without needing power (as far as possible)
            private void ForcePowerOn(BaseEntity ent)
            {
                ent.SetFlag(BaseEntity.Flags.On, true);
                ent.SendNetworkUpdate();
            }

            // toggle the light on/off (keep the entity, just flip the power flag = for dimming surplus drones)
            private bool _lightActive = true;
            public void SetLightActive(bool active)
            {
                if (_light == null || _light.IsDestroyed) return;
                if (_lightActive == active) return; // do nothing if unchanged (avoid wasteful net updates)
                _lightActive = active;
                _light.SetFlag(BaseEntity.Flags.On, active);
                _light.SendNetworkUpdate();
            }

            private void OnDestroy()
            {
                if (_light != null && !_light.IsDestroyed) _light.Kill();
                if (_fire != null && !_fire.IsDestroyed) _fire.Kill();
                _plugin?.UnregisterAgent(this);
            }
        }

        // =====================================================================
        //  Wave-based minigame
        // =====================================================================
        private WaveGame _game;

        // Throw a bomb toward the player (leading the shot). Give it horizontal velocity so it hits even while orbiting.
        public void DropBomb(Drone drone, Vector3 targetPos)
        {
            if (config.BombPrefabs == null || config.BombPrefabs.Count == 0) return;
            string prefab = config.BombPrefabs[UnityEngine.Random.Range(0, config.BombPrefabs.Count)];
            Vector3 spawnPos = drone.transform.position + Vector3.down * 0.5f;
            var bomb = GameManager.server.CreateEntity(prefab, spawnPos, Quaternion.identity);
            if (bomb == null) return;
            // give it a velocity that closes the horizontal distance within the fall time (fuse)
            Vector3 horiz = targetPos - spawnPos; horiz.y = 0f;
            float fuse = Mathf.Max(0.5f, config.BombFuse);
            Vector3 lead = horiz / fuse; // horizontal lead
            Vector3 inherit = drone.body != null ? drone.body.linearVelocity * 0.5f : Vector3.zero;
            Vector3 vel = lead + inherit + Vector3.down * config.BombDropSpeed;
            bomb.creatorEntity = null;
            bomb.Spawn();
            bomb.SetVelocity(vel);
            if (bomb is TimedExplosive te)
                te.SetFuse(fuse);
        }

        // Charger: explodes at its own position and self-destructs
        public void DetonateCharger(Drone drone, WaveGame game)
        {
            if (drone == null || drone.IsDestroyed) return;
            string prefab = config.BombPrefabs != null && config.BombPrefabs.Count > 0
                ? config.BombPrefabs[0]
                : "assets/prefabs/weapons/f1 grenade/grenade.f1.deployed.prefab";
            var bomb = GameManager.server.CreateEntity(prefab, drone.transform.position, Quaternion.identity);
            if (bomb != null)
            {
                bomb.creatorEntity = null;
                bomb.Spawn();
                if (bomb is TimedExplosive te) te.SetFuse(0.2f);
            }
            drone.Kill();
        }

        // Shared gunner logic: NPC-style hitscan. Each pellet is fired down a randomized aim cone and
        // occluded by solid geometry (world/terrain/construction/deployed), so cover blocks the shot and
        // distance + spread make it dodgeable. A shot only hurts the player if its line actually passes
        // through the body capsule AND nothing blocks the path.
        public void EnemyShoot(Drone drone, BasePlayer target, float damage, float spreadDeg, int pellets)
        {
            if (drone == null || drone.IsDestroyed || target == null || target.IsDead()) return;
            Vector3 from = drone.transform.position + Vector3.down * 0.3f;
            // sample points on the player's body for line-of-sight and hit tests
            Vector3 basePos = target.transform.position;
            Vector3 eyes = target.eyes != null ? target.eyes.position : basePos + Vector3.up * 1.5f;
            Vector3 chest = basePos + Vector3.up * 0.9f;
            Vector3 hips = basePos + Vector3.up * 0.3f;
            float dist = Vector3.Distance(from, chest);
            if (dist > config.GunRange) return; // out of range

            int coverMask = Rust.Layers.Mask.World | Rust.Layers.Mask.Terrain
                          | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed;

            // Fire gate: like an NPC, don't shoot at all unless at least one body point is visible.
            // Standing under a solid (or holey) roof or inside a container blocks all three -> no fire.
            if (config.GunRequireLoS
                && IsOccluded(from, eyes, coverMask)
                && IsOccluded(from, chest, coverMask)
                && IsOccluded(from, hips, coverMask))
                return;

            // gunshot + muzzle flash (so the player can tell they're being shot at)
            MaybeEffect(config.GunEffect, from);

            Vector3 baseDir = (chest - from) / Mathf.Max(0.001f, dist);
            Vector3 origin = from + baseDir * 1.0f; // start just past the drone's own collider
            float bodyR = Mathf.Max(0.05f, config.GunHitboxRadius);
            float spread = Mathf.Max(config.GunMinSpread, spreadDeg);

            for (int i = 0; i < Mathf.Max(1, pellets); i++)
            {
                // deviate the shot inside an aim cone; at range the same angle misses by a wider margin
                Vector3 dir = Quaternion.Euler(
                    UnityEngine.Random.Range(-spread, spread),
                    UnityEngine.Random.Range(-spread, spread), 0f) * baseDir;

                // closest approach of this pellet's line to the chest -> did it geometrically hit the body?
                float t = Vector3.Dot(chest - origin, dir);
                Vector3 closest = origin + dir * Mathf.Clamp(t, 0f, dist + bodyR);
                bool wouldHit = t > 0f && Vector3.Distance(closest, chest) <= bodyR;

                if (wouldHit)
                {
                    // cover between the drone and the player blocks the hit
                    if (Physics.Raycast(origin, (chest - origin).normalized, out RaycastHit hit, dist - 0.1f, coverMask))
                    {
                        MaybeEffect(config.GunImpactEffect, hit.point);
                    }
                    else
                    {
                        target.Hurt(damage, Rust.DamageType.Bullet, drone, useProtection: config.GunUseProtection);
                        MaybeEffect(config.GunImpactEffect, chest);
                    }
                }
                else
                {
                    // stray shot: show it landing wherever the line ends (near the player or on cover)
                    if (Physics.Raycast(origin, dir, out RaycastHit miss, dist + 2f, coverMask))
                        MaybeEffect(config.GunImpactEffect, miss.point);
                    else
                        MaybeEffect(config.GunImpactEffect, closest);
                }
            }
        }

        // True if solid geometry blocks the straight line from a to b.
        private bool IsOccluded(Vector3 a, Vector3 b, int mask)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return false;
            return Physics.Raycast(a, d / len, len - 0.05f, mask);
        }

        // Boss: fire a rocket (real explosion) at the player
        public void FireRocket(Drone drone, BasePlayer target)
        {
            if (drone == null || drone.IsDestroyed || target == null || target.IsDead()) return;
            Vector3 from = drone.transform.position + Vector3.down * 0.5f;
            Vector3 to = target.transform.position + Vector3.up * 1f;
            Vector3 dir = (to - from).normalized;
            var rocket = GameManager.server.CreateEntity(config.RocketPrefab, from, Quaternion.LookRotation(dir));
            if (rocket == null) return;
            rocket.creatorEntity = null;
            var proj = rocket.GetComponent<ServerProjectile>();
            proj?.InitializeVelocity(dir * config.RocketSpeed);
            rocket.Spawn();
        }

        private void MaybeEffect(string effect, Vector3 pos)
        {
            if (!string.IsNullOrEmpty(effect))
                Effect.server.Run(effect, pos);
        }

        // Scatter loot on the ground when a minigame enemy is killed. Optionally also spawn a container
        // for an external loot-manager plugin (Loottable) to populate.
        private void DropLoot(EnemyType type, Vector3 pos)
        {
            if (!config.LootEnabled) return;

            // Optional bridge: a loot-manager plugin populates this container if it is configured for the prefab.
            if (!string.IsNullOrEmpty(config.LootContainerPrefab))
            {
                var cont = GameManager.server.CreateEntity(config.LootContainerPrefab, pos + Vector3.up * 0.2f);
                cont?.Spawn();
            }

            if (config.LootTables == null) return;
            if (!config.LootTables.TryGetValue(type.ToString(), out var entries) || entries == null) return;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Shortname)) continue;
                if (UnityEngine.Random.value > Mathf.Clamp01(e.Chance)) continue;
                int amount = UnityEngine.Random.Range(e.Min, Mathf.Max(e.Min, e.Max) + 1);
                if (amount <= 0) continue;
                var def = ItemManager.FindItemDefinition(e.Shortname);
                if (def == null) { PrintWarning($"Loot: unknown item shortname '{e.Shortname}' (check 'Loot - Tables per enemy type')"); continue; }
                var item = ItemManager.Create(def, amount, e.SkinId);
                if (item == null) continue;
                if (!string.IsNullOrEmpty(e.DisplayName)) item.name = e.DisplayName;
                Vector3 dropPos = pos + UnityEngine.Random.insideUnitSphere * config.LootScatterRadius;
                dropPos.y = pos.y + 0.3f;
                item.Drop(dropPos, UnityEngine.Random.insideUnitSphere * 1.2f + Vector3.up * 0.5f);
            }
        }

        // score handling for downing enemy drones
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var agent = entity?.GetComponent<DroneAgent>();
            if (agent == null || agent.Mode != AgentMode.Enemy || _game == null) return;
            var attacker = info?.InitiatorPlayer;
            // Reward: only for a real player kill during a running game (skips game-stop cleanup and
            // the charger's self-detonation, which have no player initiator).
            if (_game.Running && attacker != null)
                DropLoot(agent.Enemy, entity.transform.position);
            _game.OnBomberDestroyed(agent, attacker);
        }

        [ChatCommand("dronegame")]
        private void CmdDroneGame(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermAdmin)) { Msg(player, "NoPermission"); return; }
            string sub = args.Length > 0 ? args[0].ToLower() : "";
            switch (sub)
            {
                case "start":
                    {
                        if (_game != null && _game.Running) { Msg(player, "Game_Running"); return; }
                        int waves = args.Length >= 2 && int.TryParse(args[1], out int w) ? w : config.DefaultWaves;
                        Vector3 center = GetGroundPointInFront(player);
                        _game = new WaveGame(this, center, config);
                        _game.Start(waves);
                        Msg(player, "Game_Started", waves, center);
                        break;
                    }
                case "boss":
                    {
                        float scale = args.Length >= 2 && float.TryParse(args[1], out var sc) && sc > 0 ? sc : config.BossScale;
                        if (_game == null || !_game.Running)
                        {
                            _game = new WaveGame(this, GetGroundPointInFront(player), config);
                            _game.StartEmpty();
                        }
                        _game.SpawnBoss(scale);
                        Msg(player, "Boss_Spawned", scale);
                        break;
                    }
                case "stop":
                    if (_game == null || !_game.Running) { Msg(player, "Game_NotRunning"); return; }
                    _game.Stop();
                    Msg(player, "Game_Stopped");
                    break;
                case "status":
                    if (_game == null || !_game.Running) { Msg(player, "Game_NotRunning"); return; }
                    Reply(player, _game.StatusText(player));
                    break;
                case "fx":
                    {
                        if (args.Length < 2) { Msg(player, "Usage_GameFx"); return; }
                        string fx = string.Join(" ", args.Skip(1)); // handle paths that contain spaces
                        Vector3 pos = GetGroundPointInFront(player) + Vector3.up * 1.5f;
                        Effect.server.Run(fx, pos, Vector3.up, null, true);
                        Msg(player, "Game_FxPlayed", fx);
                        break;
                    }
                default:
                    Msg(player, "Help_Game");
                    break;
            }
        }

        public class WaveGame
        {
            public Vector3 Center;
            public float Radius;
            public float AttackHeight;
            public bool Running { get; private set; }
            public int CurrentWave { get; private set; }
            public int TotalWaves { get; private set; }

            private readonly DroneShow _plugin;
            private readonly Configuration _cfg;
            private readonly List<DroneAgent> _bombers = new List<DroneAgent>();
            private readonly Dictionary<ulong, int> _scores = new Dictionary<ulong, int>();

            public WaveGame(DroneShow plugin, Vector3 center, Configuration cfg)
            {
                _plugin = plugin;
                _cfg = cfg;
                Center = center;
                Radius = cfg.ArenaRadius;
                AttackHeight = cfg.AttackHeight;
            }

            public void Start(int waves)
            {
                Running = true;
                TotalWaves = waves;
                CurrentWave = 0;
                NextWave();
            }

            private void NextWave()
            {
                if (!Running) return;
                CurrentWave++;
                if (CurrentWave > TotalWaves) { Win(); return; }

                bool bossWave = CurrentWave == TotalWaves; // the final wave is the boss
                // spawn count per type (grows with the wave)
                int charger = _cfg.ChargerBase + (CurrentWave - 1) * _cfg.ChargerPerWave;
                int bomber = _cfg.BomberBase + (CurrentWave - 1) * _cfg.BomberPerWave;
                int gunner = _cfg.GunnerBase + (CurrentWave - 1) * _cfg.GunnerPerWave;

                var gunnerStyles = new[] { EnemyType.GunnerRapid, EnemyType.GunnerSniper, EnemyType.GunnerShotgun };
                var spawnList = new List<EnemyType>();
                for (int i = 0; i < charger; i++) spawnList.Add(EnemyType.Charger);
                for (int i = 0; i < bomber; i++) spawnList.Add(EnemyType.Bomber);
                for (int i = 0; i < gunner; i++) spawnList.Add(gunnerStyles[UnityEngine.Random.Range(0, gunnerStyles.Length)]);
                if (bossWave) spawnList.Add(EnemyType.Boss);

                int total = spawnList.Count;
                if (bossWave)
                    _plugin.BroadcastLang("Wave_BossAnnounce", CurrentWave, TotalWaves);
                else
                    _plugin.BroadcastLang("Wave_Announce", CurrentWave, TotalWaves, total, charger, bomber, gunner);

                for (int i = 0; i < total; i++)
                {
                    float ang = (2f * Mathf.PI / Mathf.Max(1, total)) * i;
                    Vector3 pos = Center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * Radius + Vector3.up * AttackHeight;
                    SpawnEnemy(spawnList[i], pos, i, total, _cfg.BossScale);
                }
            }

            // spawn one enemy (the boss is scaled before Spawn)
            private void SpawnEnemy(EnemyType type, Vector3 pos, int index, int total, float bossScale)
            {
                float scale = type == EnemyType.Boss ? bossScale : 1f;
                var drone = _plugin.SpawnDrone(pos, Quaternion.identity, 0, scale);
                if (drone == null) return;
                var agent = drone.gameObject.AddComponent<DroneAgent>();
                agent.InitEnemy(_plugin, this, type, index, total);
                _plugin.DisableCollisions(drone);
                _bombers.Add(agent);
            }

            // just set the running flag (for testing a single boss)
            public void StartEmpty()
            {
                Running = true;
                TotalWaves = 0;
                CurrentWave = 0;
            }

            // spawn just one boss (for the command; size can be specified)
            public void SpawnBoss(float scale)
            {
                Vector3 pos = Center + Vector3.up * AttackHeight;
                SpawnEnemy(EnemyType.Boss, pos, 0, 1, scale);
            }

            public void OnBomberDestroyed(DroneAgent agent, BasePlayer attacker)
            {
                _bombers.Remove(agent);
                if (attacker != null)
                {
                    ulong id = (ulong)attacker.userID;
                    if (!_scores.ContainsKey(id)) _scores[id] = 0;
                    _scores[id]++;
                }
                // advance to the next wave once no bombers remain alive
                _bombers.RemoveAll(a => a == null || a.Drone == null || a.Drone.IsDestroyed);
                if (Running && _bombers.Count == 0)
                    _plugin.Once(_cfg.WaveInterval, NextWave);
            }

            public BasePlayer FindNearestTarget(Vector3 from)
            {
                BasePlayer best = null;
                float bestSqr = float.MaxValue;
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.IsConnected || p.IsDead() || p.IsSleeping()) continue;
                    // only players inside the arena
                    if (Vector3Ex.Distance2D(p.transform.position, Center) > Radius * 1.5f) continue;
                    float sqr = (p.transform.position - from).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; best = p; }
                }
                return best;
            }

            public void Win()
            {
                _plugin.BroadcastLang("Game_Clear");
                Stop();
            }

            public void Stop()
            {
                Running = false;
                foreach (var a in _bombers.ToList())
                    if (a != null && a.Drone != null && !a.Drone.IsDestroyed) a.Drone.Kill();
                _bombers.Clear();
                // show the score
                if (_scores.Count > 0)
                {
                    var top = _scores.OrderByDescending(kv => kv.Value).Take(5).ToList();
                    // Each player sees the ranking in their own language
                    foreach (var p in BasePlayer.activePlayerList)
                    {
                        if (p == null) continue;
                        var sb = new System.Text.StringBuilder(_plugin.Lang("Score_Header", p) + "\n");
                        foreach (var kv in top)
                        {
                            var pl = BasePlayer.FindByID(kv.Key);
                            sb.AppendLine(_plugin.Lang("Score_Item", p, pl?.displayName ?? kv.Key.ToString(), kv.Value));
                        }
                        p.ChatMessage(sb.ToString());
                    }
                }
            }

            public string StatusText(BasePlayer player)
                => _plugin.Lang("Status", player, CurrentWave, TotalWaves, _bombers.Count);
        }

        // =====================================================================
        //  Pattern: data storage + UI editor
        // =====================================================================
        private const string PatternUi = "droneshow.patternui";

        public class PatternData
        {
            [JsonProperty("Patterns")]
            public Dictionary<string, List<string>> Patterns =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        private PatternData _patternData;

        private class PatternEdit
        {
            public string Name; public int W; public int H; public bool[] Cells; public string Group;
            public int Brush = 1;     // brush radius 1/2/3 (side = 2*Brush-1)
            public int Mode;          // 0=draw 1=erase 2=rectangle
            public int RectFirst = -1; // first corner of the rectangle
        }
        private readonly Dictionary<ulong, PatternEdit> _editors = new Dictionary<ulong, PatternEdit>();

        // Read data/DroneShow_Patterns.json from disk. Returns the loaded count, or -1 on parse error.
        // On error we KEEP the current in-memory patterns (never wipe good data because of a bad hand-edit).
        private int LoadPatternData()
        {
            PatternData loaded;
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<PatternData>("DroneShow_Patterns");
            }
            catch (Exception ex)
            {
                // Surface the parse error instead of silently swallowing it. A malformed manual edit
                // (stray comma, full-width quotes, etc.) lands here; keep existing patterns intact.
                PrintWarning($"Failed to read data/DroneShow_Patterns.json - keeping current patterns. Error: {ex.Message}");
                if (_patternData == null) _patternData = new PatternData();
                return -1;
            }
            // Rebuild the case-insensitive dictionary from the deserialized (ordinal) one.
            var fresh = new PatternData();
            if (loaded?.Patterns != null)
                foreach (var kv in loaded.Patterns)
                    if (kv.Value != null) fresh.Patterns[kv.Key] = kv.Value;
            _patternData = fresh;
            return _patternData.Patterns.Count;
        }
        private void SavePatternData() => Interface.Oxide.DataFileSystem.WriteObject("DroneShow_Patterns", _patternData);

        private static List<string> CellsToRows(PatternEdit e)
        {
            var rows = new List<string>(e.H);
            for (int r = 0; r < e.H; r++)
            {
                var sb = new StringBuilder(e.W);
                for (int c = 0; c < e.W; c++) sb.Append(e.Cells[r * e.W + c] ? '#' : '.');
                rows.Add(sb.ToString());
            }
            return rows;
        }

        private static PatternEdit RowsToEdit(string name, List<string> rows)
        {
            int h = Mathf.Max(1, rows.Count), w = 1;
            foreach (var r in rows) w = Mathf.Max(w, r.Length);
            var e = new PatternEdit { Name = name, W = w, H = h, Cells = new bool[w * h] };
            for (int r = 0; r < rows.Count; r++)
            {
                string row = rows[r];
                for (int c = 0; c < row.Length && c < w; c++)
                {
                    char ch = row[c];
                    e.Cells[r * w + c] = (ch == '#' || ch == '1' || ch == '*');
                }
            }
            return e;
        }

        // ---- /dronepattern (UI editor / management) ----
        [ChatCommand("dronepattern")]
        private void CmdDronePattern(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player, PermUse)) { Msg(player, "NoPermission"); return; }
            string sub = args.Length > 0 ? args[0].ToLower() : "help";
            switch (sub)
            {
                case "new":
                    if (args.Length < 2) { Msg(player, "Usage_PatternNew"); return; }
                    int maxSize = Mathf.Clamp(config.PatternEditorMaxSize, 4, 64);
                    // default width/height to the max drawable size when omitted
                    int w = maxSize, h = maxSize;
                    if (args.Length >= 4)
                    {
                        if (!int.TryParse(args[2], out w) || !int.TryParse(args[3], out h)) { Msg(player, "Pattern_SizeNumber"); return; }
                        if (w > maxSize || h > maxSize) Msg(player, "Pattern_SizeClamped", maxSize);
                    }
                    w = Mathf.Clamp(w, 1, maxSize); h = Mathf.Clamp(h, 1, maxSize);
                    _editors[(ulong)player.userID] = new PatternEdit { Name = args[1], W = w, H = h, Cells = new bool[w * h] };
                    OpenEditor(player);
                    Msg(player, "Pattern_Created", args[1], w, h);
                    break;
                case "edit":
                    if (args.Length < 2) { Msg(player, "Usage_PatternEdit"); return; }
                    if (!_patternData.Patterns.TryGetValue(args[1], out var erows)) { Msg(player, "Pattern_NotFound", args[1]); return; }
                    _editors[(ulong)player.userID] = RowsToEdit(args[1], erows);
                    OpenEditor(player);
                    Msg(player, "Pattern_Editing", args[1]);
                    break;
                case "link":
                    if (args.Length < 2) { Msg(player, "Usage_PatternLink"); return; }
                    if (!_editors.TryGetValue((ulong)player.userID, out var el)) { Msg(player, "Pattern_OpenFirst"); return; }
                    if (!_fleets.ContainsKey(args[1])) { Msg(player, "Pattern_GroupNotFound", args[1]); return; }
                    el.Group = args[1];
                    PreviewIfLinked(player, el);
                    Msg(player, "Pattern_Linked", args[1]);
                    break;
                case "list":
                    if (_patternData.Patterns.Count == 0) { Msg(player, "Pattern_ListEmpty"); return; }
                    Msg(player, "Pattern_List", string.Join(", ", _patternData.Patterns.Keys));
                    break;
                case "delete":
                    if (args.Length < 2) { Msg(player, "Usage_PatternDelete"); return; }
                    if (_patternData.Patterns.Remove(args[1])) { SavePatternData(); Msg(player, "Pattern_Deleted", args[1]); }
                    else Msg(player, "Pattern_DeleteNotFound");
                    break;
                case "reload":
                    // Re-read data/DroneShow_Patterns.json from disk so manual edits apply without a full plugin reload.
                    int n = LoadPatternData();
                    if (n < 0) Msg(player, "Pattern_ReloadError");
                    else Msg(player, "Pattern_Reloaded", n);
                    break;
                default:
                    Msg(player, "Help_Pattern");
                    break;
            }
        }

        [ConsoleCommand("dronepattern.toggle")]
        private void CcPatternToggle(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            if (!_editors.TryGetValue((ulong)p.userID, out var e)) return;
            int i = arg.GetInt(0, -1);
            if (i < 0 || i >= e.Cells.Length) return;
            ApplyPaint(p, e, i);
            PreviewIfLinked(p, e);
        }

        // handle a single click (paint cells according to mode/brush/rectangle)
        private void ApplyPaint(BasePlayer p, PatternEdit e, int idx)
        {
            if (e.Mode == 2) // rectangle: click 2 points to fill the inside
            {
                if (e.RectFirst < 0) { e.RectFirst = idx; UpdateCell(p, e, idx); return; }
                int x0 = e.RectFirst % e.W, y0 = e.RectFirst / e.W, x1 = idx % e.W, y1 = idx / e.W;
                e.RectFirst = -1;
                int xa = Mathf.Min(x0, x1), xb = Mathf.Max(x0, x1), ya = Mathf.Min(y0, y1), yb = Mathf.Max(y0, y1);
                // update only the changed cells in a batch (avoids a full clear flickering the screen)
                var cc = new CuiElementContainer();
                bool any = false;
                for (int y = ya; y <= yb; y++)
                    for (int x = xa; x <= xb; x++)
                    {
                        int ni = y * e.W + x;
                        if (e.Cells[ni]) continue;
                        e.Cells[ni] = true;
                        CuiHelper.DestroyUi(p, CellName(ni));
                        AddCell(cc, e, ni);
                        any = true;
                    }
                if (any) CuiHelper.AddUi(p, cc);
                return;
            }
            // draw (ON) / erase (OFF) + brush
            bool val = e.Mode == 0;
            int cx = idx % e.W, cy = idx / e.W, r = e.Brush - 1;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= e.W || ny >= e.H) continue;
                    int ni = ny * e.W + nx;
                    if (e.Cells[ni] != val) { e.Cells[ni] = val; UpdateCell(p, e, ni); }
                }
            }
        }

        [ConsoleCommand("dronepattern.mode")]
        private void CcPatternMode(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            if (!_editors.TryGetValue((ulong)p.userID, out var e)) return;
            e.Mode = (e.Mode + 1) % 3;
            e.RectFirst = -1;
            UpdateToolbar(p, e);
        }

        [ConsoleCommand("dronepattern.brush")]
        private void CcPatternBrush(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            if (!_editors.TryGetValue((ulong)p.userID, out var e)) return;
            e.Brush = e.Brush % 3 + 1;
            UpdateToolbar(p, e);
        }

        [ConsoleCommand("dronepattern.save")]
        private void CcPatternSave(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            if (!_editors.TryGetValue((ulong)p.userID, out var e)) return;
            _patternData.Patterns[e.Name] = CellsToRows(e);
            SavePatternData();
            Msg(p, "Pattern_Saved", e.Name);
        }

        [ConsoleCommand("dronepattern.clear")]
        private void CcPatternClear(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            if (!_editors.TryGetValue((ulong)p.userID, out var e)) return;
            Array.Clear(e.Cells, 0, e.Cells.Length);
            RefreshAllCells(p, e);
            PreviewIfLinked(p, e);
        }

        [ConsoleCommand("dronepattern.close")]
        private void CcPatternClose(ConsoleSystem.Arg arg)
        {
            var p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, PatternUi);
            _editors.Remove((ulong)p.userID);
        }

        // apply the grid being edited to the real drones (linked group) instantly
        private void PreviewIfLinked(BasePlayer player, PatternEdit e)
        {
            if (e == null || string.IsNullOrEmpty(e.Group)) return;
            if (!_fleets.TryGetValue(e.Group, out var fleet)) return;
            var points = Glyphs.LayoutRows(CellsToRows(e), config.TextDotSpacing);
            if (points.Count == 0 || points.Count > config.MaxDronesPerGroup) return;
            fleet.StopShow();
            AdjustFleetCount(fleet, points.Count);
            fleet.ApplyPoints(points);
            fleet.SetLights(true, config.LightPrefab);
            fleet.RefreshParkedLights();
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static string CellName(int idx) => "droneshow.patternui.cell." + idx;

        // Build the parent (cursor-enabled) panel, buttons, and all cells. Each cell is placed by name directly under the root
        // (no transparent container covers them, so the buttons underneath remain clickable).
        private void OpenEditor(BasePlayer player)
        {
            if (!_editors.TryGetValue((ulong)player.userID, out var e)) return;
            CuiHelper.DestroyUi(player, PatternUi);
            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.10 0.96" },
                RectTransform = { AnchorMin = "0.25 0.13", AnchorMax = "0.75 0.92" },
                CursorEnabled = true
            }, "Overlay", PatternUi);

            c.Add(new CuiLabel
            {
                Text = { Text = Lang("Ui_EditTitle", player, e.Name, e.W, e.H), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, PatternUi);

            AddToolbar(c, e, player);
            AddEditorButton(c, "0.41 0.03", "0.595 0.13", "dronepattern.save", Lang("Ui_Save", player), "0.20 0.50 0.90 0.95");
            AddEditorButton(c, "0.605 0.03", "0.79 0.13", "dronepattern.clear", Lang("Ui_Clear", player), "0.55 0.40 0.20 0.95");
            AddEditorButton(c, "0.80 0.03", "0.985 0.13", "dronepattern.close", Lang("Ui_Close", player), "0.55 0.20 0.20 0.95");

            for (int i = 0; i < e.Cells.Length; i++) AddCell(c, e, i);
            CuiHelper.AddUi(player, c);
        }

        private const string PatternBtnMode = "droneshow.patternui.btnmode";
        private const string PatternBtnBrush = "droneshow.patternui.btnbrush";
        private string ModeLabel(PatternEdit e, BasePlayer player)
            => Lang("Ui_ModePrefix", player) + (e.Mode == 0 ? Lang("Ui_ModeDraw", player) : e.Mode == 1 ? Lang("Ui_ModeErase", player) : Lang("Ui_ModeRect", player));

        // Mode/brush buttons (show current state, toggle on click)
        private void AddToolbar(CuiElementContainer c, PatternEdit e, BasePlayer player)
        {
            c.Add(new CuiButton
            {
                Button = { Command = "dronepattern.mode", Color = "0.25 0.45 0.30 0.95" },
                RectTransform = { AnchorMin = "0.015 0.03", AnchorMax = "0.205 0.13" },
                Text = { Text = ModeLabel(e, player), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, PatternUi, PatternBtnMode);
            c.Add(new CuiButton
            {
                Button = { Command = "dronepattern.brush", Color = "0.25 0.35 0.45 0.95" },
                RectTransform = { AnchorMin = "0.215 0.03", AnchorMax = "0.40 0.13" },
                Text = { Text = Lang("Ui_Brush", player, e.Brush), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, PatternUi, PatternBtnBrush);
        }

        private void UpdateToolbar(BasePlayer player, PatternEdit e)
        {
            CuiHelper.DestroyUi(player, PatternBtnMode);
            CuiHelper.DestroyUi(player, PatternBtnBrush);
            var c = new CuiElementContainer();
            AddToolbar(c, e, player);
            CuiHelper.AddUi(player, c);
        }

        // add a button for a single cell (parent = root, name = CellName)
        private void AddCell(CuiElementContainer c, PatternEdit e, int idx)
        {
            float gx0 = 0.04f, gx1 = 0.96f, gy0 = 0.17f, gy1 = 0.9f;
            float cw = (gx1 - gx0) / e.W, ch = (gy1 - gy0) / e.H;
            int col = idx % e.W, r = idx / e.W;
            bool on = e.Cells[idx];
            float x0 = gx0 + col * cw, y0 = gy1 - (r + 1) * ch;
            const float pad = 0.10f;
            c.Add(new CuiButton
            {
                Button = { Command = $"dronepattern.toggle {idx}", Color = on ? "0.30 0.85 0.40 0.95" : "0.22 0.22 0.26 0.85" },
                RectTransform = { AnchorMin = $"{F(x0 + cw * pad)} {F(y0 + ch * pad)}", AnchorMax = $"{F(x0 + cw * (1 - pad))} {F(y0 + ch * (1 - pad))}" },
                Text = { Text = "" }
            }, PatternUi, CellName(idx));
        }

        // update just that cell (prevents flicker, keeps the cursor)
        private void UpdateCell(BasePlayer player, PatternEdit e, int idx)
        {
            CuiHelper.DestroyUi(player, CellName(idx));
            var c = new CuiElementContainer();
            AddCell(c, e, idx);
            CuiHelper.AddUi(player, c);
        }

        // redraw all cells (for clearing)
        private void RefreshAllCells(BasePlayer player, PatternEdit e)
        {
            var c = new CuiElementContainer();
            for (int i = 0; i < e.Cells.Length; i++)
            {
                CuiHelper.DestroyUi(player, CellName(i));
                AddCell(c, e, i);
            }
            CuiHelper.AddUi(player, c);
        }

        private void AddEditorButton(CuiElementContainer c, string min, string max, string cmd, string label, string color)
        {
            c.Add(new CuiButton
            {
                Button = { Command = cmd, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = label, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, PatternUi);
        }

        // =====================================================================
        //  Configuration
        // =====================================================================
        private Configuration config;

        // One loot line for the per-enemy-type drop table.
        public class LootEntry
        {
            [JsonProperty("Item shortname")] public string Shortname = "scrap";
            [JsonProperty("Min amount")] public int Min = 1;
            [JsonProperty("Max amount")] public int Max = 1;
            [JsonProperty("Chance (0-1)")] public float Chance = 1f;
            [JsonProperty("Skin ID")] public ulong SkinId = 0;
            [JsonProperty("Custom name (optional, empty = default)")] public string DisplayName = "";
        }

        public class Configuration
        {
            [JsonProperty("Drone prefab path")]
            public string DronePrefab = "assets/prefabs/deployable/drone/drone.deployed.prefab";

            [JsonProperty("Max drones per group")]
            public int MaxDronesPerGroup = 2500;

            [JsonProperty("Default spawn height (m)")]
            public float DefaultSpawnHeight = 35f;

            [JsonProperty("Default formation spacing (m)")]
            public float DefaultSpacing = 2.5f;

            [JsonProperty("Text - Dot spacing (m)")]
            public float TextDotSpacing = 1.8f;

            [JsonProperty("Pattern editor max grid size (UI cells; large = heavy)")]
            public int PatternEditorMaxSize = 32;

            [JsonProperty("Drone move speed override (higher=tighter formation, 0=vanilla, global)")]
            public float DroneMoveSpeedOverride = 30f;

            [JsonProperty("Drone altitude speed override (0=vanilla, global)")]
            public float DroneAltitudeSpeedOverride = 30f;

            [JsonProperty("Show drones invulnerable")]
            public bool ShowDronesInvulnerable = true;

            [JsonProperty("Disable drone-to-drone collisions")]
            public bool IgnoreDroneCollisions = true;

            [JsonProperty("Light prefab (empty to disable)")]
            public string LightPrefab = "assets/prefabs/deployable/playerioents/lights/simplelight.prefab";

            // ---- Minigame: general ----
            [JsonProperty("Minigame - Default wave count")]
            public int DefaultWaves = 5;

            [JsonProperty("Minigame - Arena radius (m)")]
            public float ArenaRadius = 40f;

            [JsonProperty("Minigame - Attack height (m)")]
            public float AttackHeight = 20f;

            [JsonProperty("Minigame - Delay between waves (sec)")]
            public float WaveInterval = 5f;

            // ---- Orbit (formation circling) ----
            [JsonProperty("Orbit - Radius (m)")]
            public float OrbitRadius = 12f;

            [JsonProperty("Orbit - Speed (rad/s)")]
            public float OrbitSpeed = 0.8f;

            // ---- Spawn counts (increase per wave) ----
            [JsonProperty("Spawn count - Charger base")]
            public int ChargerBase = 1;
            [JsonProperty("Spawn count - Charger per wave")]
            public int ChargerPerWave = 1;
            [JsonProperty("Spawn count - Bomber base")]
            public int BomberBase = 2;
            [JsonProperty("Spawn count - Bomber per wave")]
            public int BomberPerWave = 1;
            [JsonProperty("Spawn count - Gunner base")]
            public int GunnerBase = 1;
            [JsonProperty("Spawn count - Gunner per wave")]
            public int GunnerPerWave = 1;

            // ---- Bomber type ----
            [JsonProperty("Bomber - Health")]
            public float BomberHealth = 60f;
            [JsonProperty("Bomber - Drop interval (sec)")]
            public float BombInterval = 4f;
            [JsonProperty("Bomber - Drop speed (m/s)")]
            public float BombDropSpeed = 6f;
            [JsonProperty("Bomber - Fuse (sec)")]
            public float BombFuse = 2.5f;
            // ObjectCreationHandling.Replace: without it, Json.NET APPENDS the JSON entries to this
            // pre-populated list on every load, so the config grows on each reload.
            [JsonProperty("Bomber - Bomb prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BombPrefabs = new List<string>
            {
                "assets/prefabs/weapons/f1 grenade/grenade.f1.deployed.prefab",
                "assets/prefabs/tools/c4/explosive.timed.deployed.prefab"
            };

            // ---- Charger type ----
            [JsonProperty("Charger - Health")]
            public float ChargerHealth = 30f;
            [JsonProperty("Charger - Explode distance (m)")]
            public float ChargerExplodeRadius = 3f;

            // ---- Gunner: shared ----
            [JsonProperty("Gun - Range (m)")]
            public float GunRange = 120f;
            [JsonProperty("Gun - Respect armor (true=armor reduces damage)")]
            public bool GunUseProtection = true;
            [JsonProperty("Gun - Muzzle effect (empty to disable)")]
            public string GunEffect = "assets/prefabs/weapons/ak47u/effects/attack.prefab";
            [JsonProperty("Gun - Impact effect (empty to disable)")]
            public string GunImpactEffect = "";
            // ---- Gun accuracy / dodge (like an NPC: cover blocks, distance/spread let you dodge) ----
            [JsonProperty("Gun - Require line of sight to fire (won't shoot through roofs/walls)")]
            public bool GunRequireLoS = true;
            [JsonProperty("Gun - Min spread (deg, higher = easier to dodge, prevents pin-point aim)")]
            public float GunMinSpread = 1.5f;
            [JsonProperty("Gun - Target hitbox radius (m, smaller = easier to dodge)")]
            public float GunHitboxRadius = 0.45f;

            // ---- Gunner: rapid ----
            [JsonProperty("Gunner Rapid - Health")]
            public float RapidHealth = 50f;
            [JsonProperty("Gunner Rapid - Fire interval (sec)")]
            public float RapidInterval = 0.4f;
            [JsonProperty("Gunner Rapid - Damage")]
            public float RapidDamage = 6f;
            [JsonProperty("Gunner Rapid - Spread (deg)")]
            public float RapidSpread = 4f;

            // ---- Gunner: sniper ----
            [JsonProperty("Gunner Sniper - Health")]
            public float SniperHealth = 40f;
            [JsonProperty("Gunner Sniper - Fire interval (sec)")]
            public float SniperInterval = 3.5f;
            [JsonProperty("Gunner Sniper - Damage")]
            public float SniperDamage = 40f;
            [JsonProperty("Gunner Sniper - Spread (deg, 0=precise)")]
            public float SniperSpread = 0f;

            // ---- Gunner: shotgun ----
            [JsonProperty("Gunner Shotgun - Health")]
            public float ShotgunHealth = 70f;
            [JsonProperty("Gunner Shotgun - Fire interval (sec)")]
            public float ShotgunInterval = 2f;
            [JsonProperty("Gunner Shotgun - Damage per pellet")]
            public float ShotgunDamage = 9f;
            [JsonProperty("Gunner Shotgun - Spread (deg)")]
            public float ShotgunSpread = 10f;
            [JsonProperty("Gunner Shotgun - Pellet count")]
            public int ShotgunPellets = 6;

            // ---- Boss ----
            [JsonProperty("Boss - Health")]
            public float BossHealth = 1500f;
            [JsonProperty("Boss - Size multiplier (1 to disable)")]
            public float BossScale = 5f;
            [JsonProperty("Boss - Orbit radius (m)")]
            public float BossOrbitRadius = 20f;
            [JsonProperty("Boss - Enrage health fraction (0-1)")]
            public float BossEnrageHealthPct = 0.4f;
            [JsonProperty("Boss Gun - Fire interval (sec)")]
            public float BossGunInterval = 0.5f;
            [JsonProperty("Boss Gun - Damage")]
            public float BossGunDamage = 9f;
            [JsonProperty("Boss Gun - Spread (deg)")]
            public float BossGunSpread = 4f;
            [JsonProperty("Boss Gun - Shots per volley")]
            public int BossGunPellets = 2;
            [JsonProperty("Boss Rocket - Interval normal (sec)")]
            public float RocketInterval = 10f;
            [JsonProperty("Boss Rocket - Interval enraged (sec)")]
            public float RocketIntervalEnraged = 3f;
            [JsonProperty("Boss Rocket - Speed (m/s)")]
            public float RocketSpeed = 25f;
            [JsonProperty("Boss Rocket - Prefab")]
            public string RocketPrefab = "assets/prefabs/ammo/rocket/rocket_basic.prefab";
            [JsonProperty("Boss - Enrage fire entity (empty to disable)")]
            public string BossFirePrefab = "assets/bundled/prefabs/oilfireballsmall.prefab";
            [JsonProperty("Boss - Fire drop entity (empty to disable)")]
            public string FireDropPrefab = "assets/bundled/prefabs/oilfireballsmall.prefab";
            [JsonProperty("Boss - Fire drop interval (sec)")]
            public float FireDropInterval = 2.5f;
            [JsonProperty("Boss - Fire drop fall speed (m/s)")]
            public float FireDropSpeed = 8f;
            // Smoke is disabled by default (empty). Effect.server.Run cannot render plume-type effects,
            // and helicopter smoke depends on the OnFire flag + a dedicated prefab, so it can't be reused.
            // To emit a puff effect, set a path such as tire_smokepuff.
            [JsonProperty("Boss - Enrage smoke puff effect (empty to disable)")]
            public string BossSmokeEffect = "";
            [JsonProperty("Boss - Smoke puff interval (sec)")]
            public float BossSmokeInterval = 0.35f;

            // ---- Loot (scattered when a minigame enemy is killed by a player) ----
            [JsonProperty("Loot - Enable drops")]
            public bool LootEnabled = true;
            [JsonProperty("Loot - Scatter radius (m)")]
            public float LootScatterRadius = 1.5f;
            // Optional bridge to a loot-manager plugin (e.g. Loottable): if set, ALSO spawn this container
            // prefab at the death spot, which that plugin (if configured for the prefab) will populate.
            // Leave empty to use only the scatter table below.
            [JsonProperty("Loot - Also spawn container prefab for a loot plugin (empty = disable)")]
            public string LootContainerPrefab = "";
            // Scatter table per enemy type. Keys: Charger / Bomber / GunnerRapid / GunnerSniper / GunnerShotgun / Boss.
            // Replace: without it Json.NET would append the JSON entries to these defaults on every reload.
            [JsonProperty("Loot - Tables per enemy type", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<LootEntry>> LootTables = new Dictionary<string, List<LootEntry>>
            {
                ["Charger"] = new List<LootEntry> { new LootEntry { Shortname = "scrap", Min = 5, Max = 15 } },
                ["Bomber"] = new List<LootEntry>
                {
                    new LootEntry { Shortname = "scrap", Min = 10, Max = 20 },
                    new LootEntry { Shortname = "gunpowder", Min = 5, Max = 15, Chance = 0.5f },
                },
                ["GunnerRapid"] = new List<LootEntry> { new LootEntry { Shortname = "scrap", Min = 10, Max = 20 } },
                ["GunnerSniper"] = new List<LootEntry> { new LootEntry { Shortname = "scrap", Min = 15, Max = 30 } },
                ["GunnerShotgun"] = new List<LootEntry> { new LootEntry { Shortname = "scrap", Min = 10, Max = 25 } },
                ["Boss"] = new List<LootEntry>
                {
                    new LootEntry { Shortname = "scrap", Min = 100, Max = 300 },
                    new LootEntry { Shortname = "metal.refined", Min = 20, Max = 50 },
                    new LootEntry { Shortname = "rifle.ak", Min = 1, Max = 1, Chance = 0.5f },
                },
            };

            // return the durability (HP) per type
            public float HealthFor(EnemyType type)
            {
                switch (type)
                {
                    case EnemyType.Charger: return ChargerHealth;
                    case EnemyType.Bomber: return BomberHealth;
                    case EnemyType.GunnerRapid: return RapidHealth;
                    case EnemyType.GunnerSniper: return SniperHealth;
                    case EnemyType.GunnerShotgun: return ShotgunHealth;
                    case EnemyType.Boss: return BossHealth;
                    default: return BomberHealth;
                }
            }
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
            }
            catch
            {
                PrintWarning("Failed to load the config. Using defaults.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);
    }
}
