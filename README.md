# Neonworks

Mods for **Nivalis Nights**.

## Lumen

A performance mod. It targets the biggest cost in a busy street and leaves the way the game
looks alone.

The game's graphics menu offers texture quality, shadow quality, shadow distance and LOD
bias. Lumen changes none of those. Instead it removes work the game is doing that never
reaches the screen:

- **Duplicate character meshes.** Every NPC renders all five of its detail levels at the
  same time - the same body drawn five times over, stacked in the same place. Lumen keeps
  the most detailed one and switches off the copies underneath.
- **Unused shadow meshes.** Each character carries extra hidden meshes whose only job is to
  cast a shadow the lighting never shows, day or night.

Both are on by default and neither changes what you see.

There is also an optional setting to hide distant crowds, which does change what you see and
is switched off unless you turn it on.

### Install

Download **`Lumen-Installer.zip`** from [Releases](../../releases), unzip it anywhere, and
double-click **Install Lumen**.

It finds your game, installs BepInEx if you do not already have it, and puts Lumen in the
right place. There is an **Uninstall Lumen** in the same folder if you change your mind.

If Windows warns you about the file, that is because it is an unsigned script rather than a
signed installer. The script is plain text and you can read every line of it
[here](Installer/install.ps1) before running it.

<details>
<summary>Installing by Hand Instead</summary>

1. Install [BepInEx 6 (IL2CPP, win-x64)](https://builds.bepinex.dev/projects/bepinex_be)
   into your Nivalis Nights folder.
2. Download `Lumen.dll` from [Releases](../../releases).
3. Put it in `Nivalis Nights\BepInEx\plugins\Lumen\`.

</details>

### Using It

Press **F10** in game.

| Key | Does |
|---|---|
| `F10` | Open and close the panel |
| Up / Down | Move between settings |
| Left / Right | Change the selected setting |

Changes apply straight away and save themselves. The panel never takes over your mouse.

### Settings

| Setting | Default | What It Does |
|---|---|---|
| Crowd Performance | On | Master switch for everything below |
| Remove Duplicate Characters | On | Free frames, no visible change |
| Skip Unused Shadow Work | On | Free frames, no visible change |
| Hide Faraway Crowds | Off | More frames, but distant people fade in as you approach |
| Borderless Window | On | Alt-tab without the screen flickering |
| Colour Theme | Synthwave | Six schemes for the panel |

**Hide Faraway Crowds** is the only setting that changes how the game looks. Lower distances
give more frames and more noticeable fade-in. Pick the point where you stop noticing.

### How Much Does It Help?

It depends entirely on your machine and where you are standing. The gain is largest in
crowded streets and close to nothing in a quiet room or indoors, because it works by
removing per-character work.

It also only helps if your processor is the thing holding you back, which is common in busy
areas of this game. If your graphics card is the limit instead, expect very little.

**Remove Duplicate Characters depends on your graphics preset.** On Very High the game draws
every character several times over and there is a lot to remove. On lower presets it often
draws them correctly already, so there is nothing to cut and this setting does very little.
That is expected rather than a fault — Lumen will not touch anything it is not sure about.

### Something Looks Wrong?

Turn **Crowd Performance** off in the panel. Everything returns to normal immediately,
without a restart.

If a problem persists, please open an [issue](../../issues) with your
`BepInEx\LogOutput.log` attached.

## Licence

MIT. See [LICENSE](LICENSE).

Not affiliated with ION LANDS or 505 Games.
