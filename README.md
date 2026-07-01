# DroneShow — RUST drone control plugin

**言語 / Language:** **English** | [日本語](README.ja.md)

An Oxide/Carbon plugin that controls multiple drones for **formation flight, lights, text/pattern displays, and drone shows**, plus a **wave-based minigame** (chargers, bombers, gunners, and a boss). It is **fully standalone** and has no external plugin dependencies.

> **Multi-language**: Chat messages use the uMod localization API. The **default is English**, and players whose language is set to Japanese see **Japanese** automatically. Translations are generated under `oxide/lang/` (`carbon/lang/` on Carbon) in `en` / `ja` folders, and you can add or edit them freely.

---

## Table of contents
- [Installation](#installation)
- [Formation / show commands](#formation--show-commands)
- [Text / pattern display](#text--pattern-display)
- [Orientation and auto showcase](#orientation-and-auto-showcase)
- [Pattern editor (UI)](#pattern-editor-ui)
- [Wave-based minigame](#wave-based-minigame)
- [Configuration](#configuration)
- [Localization](#localization)
- [How it works (technical notes)](#how-it-works-technical-notes)
- [Testing & notes](#testing--notes)

---

## Installation

1. Drop `DroneShow.cs` into your server's `carbon/plugins/` (or `oxide/plugins/` on Oxide).
2. After it compiles automatically, the config and data files are generated.
3. Grant permissions:
   ```
   oxide.grant user <SteamID> droneshow.use     # formations, text, shows, pattern editing
   oxide.grant user <SteamID> droneshow.admin   # minigame management
   ```
   (Admins (`IsAdmin`) can use all commands even without permissions.)

---

## Formation / show commands
Permission: `droneshow.use`

| Command | Description |
|---|---|
| `/drone spawn <group> <count> [height]` | Spawn a group at your aim point (default height if omitted) |
| `/drone formation <group> <line\|grid\|circle\|sphere> [spacing]` | Change the formation |
| `/drone move <group> here` / `... <x> <y> <z>` | Move the formation center |
| `/drone rotate <group> <deg>` | Formation heading (Yaw, degrees) |
| `/drone scale <group> <factor>` | Scale the formation |
| `/drone light <group> <on\|off>` | Toggle lights |
| `/drone show <group> <on\|off>` | Auto show (cycle formations + spin + lights) |
| `/drone prefabs [keyword]` | Search spawnable prefabs (default `drone`) |
| `/drone list` | List groups |
| `/drone clear <group\|all>` | Remove |

---

## Text / pattern display

Display messages or any artwork using dots (1 dot = 1 drone). **If the group does not exist it is created automatically and the required drones are spawned** (no prior `/drone spawn` needed). Surplus drones retreat underground and are **turned off** to stay hidden.

| Command | Description |
|---|---|
| `/drone text <group> <text>` | Display text with the built-in font (A-Z 0-9 and some symbols) |
| `/drone pattern <group> <saved name>` | **Display a saved pattern** (created/saved in the UI editor) |
| `/drone pattern <group> <row1> <row2> ...` | Display a raw array typed inline (each row `#`/`.` or `1`/`0`) |
| `/drone sequence <group> <switch sec> <item1>\|<item2>...` | Cycle text, shapes, and saved patterns at a fixed interval |

> **Patterns are best created with the in-game UI editor `/dronepattern`.** Click cells with the mouse to draw, name and save it, then call it up with `/drone pattern <group> <name>` (see [Pattern editor](#pattern-editor-ui)). Typing arrays into chat is a quick shortcut for simple artwork.

**Examples:**
```
/drone text show1 RUST
/dronepattern new heart 9 8      # draw a heart in the UI editor and save it
/drone pattern show1 heart       # display the saved "heart"
/drone pattern show1 .###. #...# ##### #...# #...#   # inline array (letter "A")
/drone sequence show1 5 flat HELLO | circle | pattern heart | up WORLD | sphere
```

- **Sequence items** are separated by `|` (pipe) and can freely mix these three kinds:
  - **Text** (e.g. `HELLO`) — displayed with the built-in font.
  - **Built-in shapes** (formations) — one of:
    | Name | Shape |
    |---|---|
    | `line` | Arranged in a single horizontal row |
    | `grid` | Arranged in a grid (a roughly square panel) |
    | `circle` | Arranged in a circle (ring) |
    | `sphere` | Arranged in 3D as a sphere (evenly spread via a Fibonacci sphere) |
  - **Saved pattern** — `pattern <saved name>` (or `pat <saved name>`). Recalls artwork you created and saved in the UI editor `/dronepattern`. An unknown name produces an error.
- Prefix a text or pattern item with **`flat`** (horizontal = readable from below) / **`up`** (sideways) to set the orientation for just that item (shapes are unaffected).
- For switches that change shape a lot, **4–6 seconds** is recommended (too short and it switches before the drones align, so it looks broken).
- The drone count is auto-matched to the largest item (it spawns enough for the item with the most dots).

> Supported characters: `A-Z` `0-9` `! ? . - + < >` and space. Japanese is not supported (too many dots; alphanumerics recommended). Any artwork can be created freely in the UI editor.

---

## Orientation and auto showcase

Text orientation is controlled by "tilt" (Pitch). **0° = sideways (viewed from the front) / ~90° = horizontal (readable from directly below)**.

| Command | Description |
|---|---|
| `/drone orient <group> upright` | Sideways (viewed from the front) |
| `/drone orient <group> flat` | Horizontal (readable correctly from directly below) |
| `/drone orient <group> tilt <deg>` | Tilt to an arbitrary angle |
| `/drone spin <group> <deg/sec>` / `off` | Continuous spin (show from all sides; some lag) |
| `/drone present <group> [hold sec]` / `off` | **Auto showcase**: hold still at down → front → right → back → left in order, then loop |

**Recommended:** `present` stops at each pose, so it stays clean and looks great from all sides plus directly below.
```
/drone text show1 RUST
/drone present show1 5     # loop, holding each pose for 5 seconds
```

---

## Pattern editor (UI)
Permission: `droneshow.use`

**An in-game grid UI you click with the mouse to create any artwork.** Saved artwork can be named and stored (`data/DroneShow_Patterns.json`) and recalled any number of times with `/drone pattern`.

| Command | Description |
|---|---|
| `/dronepattern new <name> <width> <height>` | Create new. Opens an empty grid UI of the given size (default limit 32×32, configurable) |
| `/dronepattern edit <name>` | Re-edit a saved pattern in the UI |
| `/dronepattern link <group>` | **Live-preview** the artwork being edited on real drones |
| `/dronepattern list` | List saved patterns |
| `/dronepattern delete <name>` | Delete |
| `/dronepattern reload` | Reload patterns from the data file (apply manual edits without a full plugin reload) |

**Using the UI:**
- Pick a **Mode** / **Brush** button at the bottom, then click grid cells to draw.
  - **Mode**: `Draw` (click to light) / `Erase` (click to clear) / `Rect` (click 2 points to fill a rectangle).
  - **Brush**: `1`/`2`/`3` … the area painted per click (1 = 1 cell, 2 = 3×3, 3 = 5×5). Use a larger brush for big areas; a rectangle is fastest for solid blocks.
- Use the **Save** / **Clear** / **Close** buttons. Only the clicked cell updates, and the cursor stays put.

> Due to Rust's UI, **click-and-drag drawing is not possible**. Instead, "Draw mode + large brush" or "rectangle fill" greatly reduces the number of clicks.

**Typical workflow (example):**
```
1) /drone spawn show1 30 35        # prepare a display group (optional; link auto-creates one)
2) /dronepattern new smile 11 9    # open an 11x9 UI editor
3) /dronepattern link show1        # while mirroring to real drones in real time...
4) click cells in the UI to draw a face
5) "Save" button -> saved as smile
6) /drone pattern show1 smile      # recall and display any time
```

> While `link` is active, the real drone formation updates instantly as you paint, so you can **design while watching the real thing**.

### About large patterns (e.g. 100×100)
- The **UI editor (click-based) limit** is `Pattern editor max grid size` (default 32, max 64). This is due to the number of CUI buttons; beyond it gets heavy/unstable. Click-editing 100×100 is impractical.
- For **larger / more precise artwork**, edit the data file **directly** for **unlimited size** (just list rows of `#`/`.`; you can also convert from an image and paste).
  - **Exact path**: `oxide/data/DroneShow_Patterns.json` (Carbon: `carbon/data/DroneShow_Patterns.json`).
  - **After editing, load it** with `/dronepattern reload` (or reload the plugin). Edits are **not** picked up automatically while the server runs.
  - **Keep the JSON valid**: use a plain-text editor with **straight ASCII quotes** (`"`), no smart/full-width quotes, and each pattern is an **array of equal-ish-length string rows**. If the file is malformed, the plugin logs a warning to the server console and **keeps your existing patterns** (it no longer wipes them silently). `/dronepattern reload` reports how many were loaded, so you can tell whether an edit parsed.
- However, **only up to `Max drones per group` lit dots (default 2500)** can be displayed (1 lit dot = 1 drone). Even a 100×100 "sparse" image with few dots can be shown, but a large densely-lit image hits the drone-count / server-load limit.

---

## Wave-based minigame
Permission: `droneshow.admin`

| Command | Description |
|---|---|
| `/dronegame start [waves]` | Start the game centered on your aim point |
| `/dronegame boss [scale]` | Spawn just one boss (for testing; size can be specified) |
| `/dronegame stop` | End |
| `/dronegame status` | Progress |

Enemy drones attack while **orbiting the player as a formation**. The count grows as waves progress, and the boss appears on the final wave. Kills add to your score, and clearing all enemies wins.

> ⚠️ **Who gets attacked**: each drone targets the **nearest connected player within the arena** (center ± `Arena radius × 1.5`, i.e. ~60m by default) — **not only the player who started the game**. There is currently **no team / permission / admin filter**, so any bystander inside the arena can be attacked. Players who are outside the radius, dead, or sleeping are ignored.

**Enemy types:**
| Type | Behavior |
|---|---|
| Charger | Charges straight at the player and self-destructs when close |
| Bomber | Orbits while throwing C4/F1 at the player |
| Rapid gunner | Fast gunfire at close range (low damage; visible muzzle/impacts) |
| Sniper gunner | High-damage, high-accuracy single shots |
| Shotgun gunner | Fires a spread of pellets at close range |
| Boss (final wave) | Large and durable. Mostly uses **guns** with occasional rockets. When low on health it **enrages**, billows black smoke, and fires rockets rapidly |

- Gunfire behaves like a normal NPC's: gunners only shoot when they can **see** you (roofs, walls, and containers block them), and each shot is fired down an **aim cone**, so you can **dodge by moving, using cover, or keeping distance**. Tune it with the `Gun - ...` config keys.
- The boss is scaled up via `networkEntityScale`, which syncs the scale to clients (no external plugin needed).

### Loot drops
When a **player kills** a minigame enemy (or the boss), it **scatters items on the ground** at the death spot. Loot is fully configurable **per enemy type**.

- Edit `Loot - Tables per enemy type` in the config. Keys: `Charger` / `Bomber` / `GunnerRapid` / `GunnerSniper` / `GunnerShotgun` / `Boss`. Each entry has `Item shortname`, `Min amount`, `Max amount`, `Chance (0-1)`, `Skin ID`, and an optional `Custom name`.
- Drops only trigger on a **real player kill during a running game** — the charger's self-detonation and game-stop cleanup drop nothing.
- **Optional loot-plugin bridge**: set `Loot - Also spawn container prefab for a loot plugin` to a container prefab. On death, DroneShow **also** spawns that container, which a loot manager such as **Loottable** can populate — **if** you have configured that plugin for the prefab. (There is no direct API call into Loottable; it works by spawning a container the loot plugin recognizes.) Leave it empty to use only the built-in scatter table.

---

## Configuration

All config keys are in English. Main ones:

### General / formation / text
| Key | Default | Description |
|---|---|---|
| `Drone prefab path` | `drone.deployed.prefab` | The drone prefab |
| `Max drones per group` | 2500 | Max drones per group (longer text needs more) |
| `Default spawn height (m)` | 35 | Default spawn height (also applied to auto-creation) |
| `Default formation spacing (m)` | 2.5 | Formation spacing |
| `Text - Dot spacing (m)` | 1.8 | Dot spacing for text/patterns |
| `Drone move speed override` | 30 | Tracking speed (higher = less drift; 0 = vanilla, global) |
| `Drone altitude speed override` | 30 | Vertical tracking speed (0 = vanilla, global) |
| `Show drones invulnerable` | true | Make show drones immune to damage |
| `Disable drone-to-drone collisions` | true | Disable collisions (prevents crashes) |
| `Light prefab (empty to disable)` | `simplelight.prefab` | Attached light |

### Minigame (excerpt)
| Key | Default | Description |
|---|---|---|
| `Minigame - Arena radius (m)` | 40 | Combat area |
| `Minigame - Attack height (m)` | 20 | Enemy hover altitude |
| `Orbit - Radius / Speed` | 12 / 0.8 | Orbit radius / speed |
| `Spawn count - <Type> base` / `... per wave` | — | Per-type initial count / per-wave growth |
| `Charger - Health` / `Bomber - Health`, etc. | 30 / 60 | **Per-type HP** |
| `Gunner Rapid/Sniper/Shotgun - Health/Damage/...` | — | Per-gunner attacks |
| `Gun - Require line of sight to fire` | true | Gunners won't shoot through roofs/walls (need to see you) |
| `Gun - Min spread (deg)` | 1.5 | Minimum aim cone — higher = easier to dodge, no pin-point aim |
| `Gun - Target hitbox radius (m)` | 0.45 | Player hit size — smaller = easier to dodge |
| `Boss - Health` / `Boss - Size multiplier` | 1500 / 3 | Boss durability / size |
| `Boss Rocket - Interval normal/enraged` | 10 / 3 | Rocket interval (normal/enraged) |
| `Boss - Enrage smoke effect / Smoke interval` | rocket_smoke / 0.6 | Black smoke while enraged |
| `Loot - Enable drops` | true | Enemies scatter loot when killed by a player |
| `Loot - Scatter radius (m)` | 1.5 | How far dropped items spread from the death spot |
| `Loot - Tables per enemy type` | (per-type) | Item drop table per enemy type / boss (shortname, min/max, chance, skin, name) |
| `Loot - Also spawn container prefab for a loot plugin` | "" | Optional: also spawn this container for Loottable etc. to fill (empty = off) |

> **Important — how config values are applied (edit the JSON, not the `.cs`)**
>
> On load the plugin reads `DroneShow.json` and **keeps the values already in it**; new keys are added with their defaults, but existing values are never overwritten. So:
> - **To change a setting**, edit the value in the **config file** and reload the plugin:
>   - File: `oxide/config/DroneShow.json` (Carbon: `carbon/config/DroneShow.json`)
>   - Then run `o.reload DroneShow` (Carbon: `c.reload DroneShow`).
> - **Editing the plugin `.cs` default does nothing to an existing server** — code defaults only apply when the JSON is generated fresh.
> - **To adopt all new defaults at once**, delete `DroneShow.json` and reload; it regenerates from the code defaults (you lose any custom values).

---

## Localization

This plugin uses the uMod [Localization API](https://umod.org/documentation/api/localization), so chat messages and the pattern editor's button labels switch based on **each player's language setting**.

- **The default language is English (`en`).** Players whose language is Japanese see **Japanese (`ja`)**.
- The language files are generated automatically on first load:
  - `oxide/lang/en/DroneShow.json` (`carbon/lang/en/DroneShow.json` on Carbon)
  - `oxide/lang/ja/DroneShow.json` (`carbon/lang/ja/DroneShow.json` on Carbon)
- **To add another language**, just create `lang/<code>/DroneShow.json` and translate each key (e.g. `de`, `fr`, `ru`, `zh-CN`). The server's default language can be changed in `oxide/config/oxide.json` (or `carbon/config/Carbon.json`).
- To reword a message, edit the value in that language's `DroneShow.json` (do not change the key).

---

## How it works (technical notes)

- **Flight control**: Setting a coordinate on `Drone.targetPosition` makes the game's physics navigation fly it automatically (same approach as `DeliveryDrone`; network sync is natural too). Each drone gets a `DroneAgent` component updated by a 0.1-second timer.
- **Faster tracking**: Raising `Drone.movementSpeedOverride` / `altitudeSpeedOverride` (global ServerVar) reduces formation/text drift while moving or rotating.
- **Collision avoidance**: `body.detectCollisions=false` keeps drones from knocking into each other / terrain and crashing (bullets are raycasts, so they can still down them).
- **Text layout**: A 5×7 dot font or any array is expanded into center-relative points and assigned to each drone. Orientation is applied on the display side via **Pitch (tilted in WorldSlot)**. Surplus drones are Parked underground and turned off.
- **Boss scaling**: `transform.localScale` + **`networkEntityScale=true`** syncs the scale to clients (set before `Spawn()`). No external plugin needed.
- **Gunfire**: NPC-style hitscan. A gunner only fires when a body point is visible (line-of-sight gate); each pellet is deviated inside an aim cone and occluded by world/terrain/construction/deployed geometry, and only damages the player if its line passes through the body capsule with a clear path. Cover blocks it, and distance/spread make it dodgeable.
- **Anti-hijack**: The `OnEntityControl` hook makes this plugin's drones impossible to pilot.

---

## Testing & notes

1. Deploy → confirm `Loaded plugin DroneShow` in the console.
2. `/drone text t1 RUST` → drones spawn automatically and display the text.
3. `/drone present t1 5` → check it's visible from each direction plus directly below.
4. `/dronepattern new heart 9 8` → check cells/save/clear/close work in the editor. `link t1` for a live preview.
5. `/dronegame boss 5` → boss size, guns, rockets, and the enrage black smoke.
6. `/dronegame start 3` → the full wave run (each enemy type).
7. Clean up with `/drone clear all` and `/dronegame stop`.

**Notes:**
- **In god mode you take no damage.** Test the minigame in a normal state.
- Effect/prefab paths that don't exist in your environment are disabled (console warning). Use `/drone prefabs <keyword>` to find real paths and swap them into the config.
- For clean-looking text, prefer `present` (still poses) over `spin` (continuous rotation).
- Manually raise `Max drones per group` / `Default spawn height` in an existing config (see above).

If you run into problems, share the error log from the server console as-is.
