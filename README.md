# Tyrian Companion Bridge (Blish HUD)

A Blish HUD module that mirrors the loot and price alerts the "Tyrian Companion" Obsidian plugin
emits (`valuable_loot`, `always_alert`, `sell_signal`, `hold_signal`) onto a Guild Wars 2 screen
notification, and reports this addon's own view of the game — map, character, whether you are in
gameplay, on a loading screen or at character select — back to the plugin, so it can mark a play
session without a click.

This is the Blish HUD half of `docs/SPEC-puente-ingame.md` (the plugin's own repo carries the spec
and the server side; this repo only carries the game-side client). The Nexus/Raidcore addon that
covers the same alerts under `tyrian-companion-nexus` is a separate repo and a separate codebase —
the two do not share a binary, only the wire contract below.

## Protocol v2: bidirectional and authenticated

Version 0.1.0 spoke protocol v1: one `hello`, no authentication, and the module never wrote to the
socket again. David decided on 2026-09-24 that Nexus and Blish HUD, with one and the same protocol,
report game context so the plugin can mark sessions automatically — which makes the channel
bidirectional, and therefore authenticated. **0.1.0 cannot talk to a plugin running protocol v2**;
this module has to be updated too.

### Pasting the token

1. In Obsidian, open the Tyrian Companion plugin's settings and find the "Token del addon" row.
   Press **Copiar token** — it copies a per-installation secret to your clipboard (generating one
   the first time, if none is set yet).
2. In Blish HUD, open this module's own settings and paste it into the **Plugin token** field.
3. Both sides now share the secret. If you ever rotate it in Obsidian (same button, after clearing
   the old entry), paste the new value here too — the old one stops working immediately, and this
   module shows "token rejected" until you do.

The token is never written to this module's log, and this module never reads it back once you've
pasted it anywhere but into memory for the next `hello`.

### What this module sends and reads now

- `hello`: client name, this module's version, a random per-process id, and the token above.
- `context`: your current map, character name and game state (`gameplay`, `loading` or
  `character_select`), sent right after the plugin's `welcome` and again whenever any of the three
  changes. Read from `GameService.Gw2Mumble` (map, character) and
  `GameService.GameIntegration.IsInGame` (gameplay vs. everything else) — nothing else: no loot, no
  inventory, no position, no combat, no AFK signal (Blish HUD does not expose one reliably).
- `heartbeat`: sent when nothing else has gone out for the plugin's own `heartbeatIntervalMs`
  (5 s by default), so a silent connection is still known to be alive.
- `bye`: sent with `reason: "game_exit"` when `GameIntegration.Gw2Closed` fires (the game process
  has actually ended) and with `reason: "addon_unload"` when you disable the module or it unloads
  without that evidence — best-effort; if the write cannot go out (a crash), the plugin treats the
  connection dropping as a loss with a 10-minute grace period, not an error.
- `alert`: painted through `ScreenNotification.ShowNotification`, deduplicated by `(server, seq)`
  so an Obsidian restart is never mistaken for "already shown" (the bug 0.1.0 had, described below).

## What it does, and does not, do

- Connects to the plugin over loopback TCP (`127.0.0.1`, port `47823` by default, configurable in
  the module's own settings, and it has to match the port set in the plugin's settings).
- Reconnects forever on a saturated backoff (`250, 500, 1000, 2000, 5000` ms), silently: no server
  listening yet is the expected state if Blish HUD starts before Obsidian does. An `auth_rejected`
  or `version_unsupported` error is the one case that does **not** retry until you change a setting
  (the token, most likely) — this module shows a notification once and waits.
- Paints the `content` string each alert line carries verbatim, through
  `ScreenNotification.ShowNotification`. It does not recompose the message from the other fields
  the wire line carries (`name`, `quantity`, `totalCopper`); `kind` only picks a notification color.
- Does **not** call the Guild Wars 2 API, with or without a key.
- Does **not** read Mumble Link beyond the map, character name and in-gameplay flag above, even
  though Blish HUD exposes far more of it (position, camera, combat state) to every module.
- Does **not** automate any action inside the game.
- Sends nothing to the plugin beyond `hello`, `context`, `heartbeat` and `bye`, ever.

Those last four points are not a style choice: they are what keeps this module inside ArenaNet's
"utility that helps players without affecting others" carve-out in its
[third-party programs policy](https://help.guildwars2.com/hc/en-us/articles/360013625034-Policy-Third-Party-Programs),
the classification the whole in-game alert bridge was authorized under.

## Building

Requires the .NET Framework 4.7.2 targeting pack and either the .NET SDK (`dotnet build`) or Visual
Studio 2019+. This project uses `PackageReference` (`TyrianCompanionBlishBridge.csproj`), so a
`dotnet restore` (or Visual Studio's own restore) pulls in `BlishHUD` 1.3.0 and its dependencies —
Blish HUD's own package carries a `build/BlishHUD.targets` file that packages the build output into
a `.bhm` automatically; there is no separate packaging script in this repo.

```
dotnet build -c Release
```

The `.bhm` lands next to the compiled DLL in `bin/Release/` (the project sets
`AppendTargetFrameworkToOutputPath` to `false`, so there is no extra `net472/` segment in the path).

### Tests

`tests/ProtocolConsoleTests/` is a small `net8.0` console project that links this repo's
`FlatJsonLine.cs` and `IngameBridgeProtocol.cs` directly (`<Compile Include>`, no project reference)
— those two files have no dependency on Blish HUD or any NuGet package, on purpose, so the wire
contract can be exercised without pulling in the game-engine dependencies (`MonoGame`, `Gw2Sharp`,
…) the module itself needs. It encodes one line of each addon-to-plugin message type and diffs it
byte-for-byte against the example trace in `docs/SPEC-puente-ingame.md`, then round-trips a handful
of plugin-to-addon lines (a well-formed `welcome`/`alert`/`error`, one with an extra key, one with a
newer `v`) through the decoder. Run it with:

```
dotnet run --project tests/ProtocolConsoleTests
```

It exits non-zero and prints the first mismatch on failure.

## Installing

Copy the built `TyrianCompanionBlishBridge.bhm` into:

```
Documents\Guild Wars 2\addons\blishhud\modules
```

Then enable the module from Blish HUD's own module list, and in the module's own settings: enable
"Show in-game alerts", check the "Plugin port" (defaults to `47823`, matching the plugin's own
default), and paste the "Plugin token" copied from the plugin's settings in Obsidian (see above —
required for protocol v2).

## Linux

**Blish HUD is not supported on Linux by its own maintainer.** From the project itself, in
[discussion #873](https://github.com/blish-hud/Blish-HUD/discussions/873): "I am not actively
pursuing any form of Linux support." Blish HUD is a separate .NET application that draws a
transparent, click-through overlay window on top of the game and needs the keyboard to keep reaching
the game underneath it; both of those go through WinAPI calls that Wine does not implement, so even
a working install is known to have the game's own window occasionally stop receiving keyboard input.

If you still want to try it under Proton/Wine, at minimum you need:

- `dotnet48` installed inside the **same Wine prefix Guild Wars 2 itself runs in** (not a separate
  prefix), via `winetricks dotnet48`.
- Compositor rules that let a second, transparent, always-on-top window sit correctly over the
  game's own borderless/fullscreen window; this is compositor- and window-manager-specific and is
  not something this module can configure for you.
- Accepting that keyboard focus passing correctly to the game while Blish HUD's window is present is
  not guaranteed.

David's own platform is Linux with Nexus (verified); Windows with Blish HUD is what has to work for
his clan's Windows players (decision of 2026-09-24). None of the Linux caveats above are part of
this module's own QA. `docs/SPEC-puente-ingame.md`'s own risk list treats a client that cannot be
verified on the primary platform as non-blocking for the rest of the in-game bridge lot.

## Source of the API used here

Written against the `BlishHUD` NuGet package `1.3.0`
(<https://www.nuget.org/packages/BlishHUD/1.3.0>) and the `dev` branch of
<https://github.com/blish-hud/Blish-HUD> at the time of writing, plus the official docs at
<https://blishhud.com/docs/modules/overview/bhm/> (the `.bhm` package format) and
<https://blishhud.com/docs/modules/overview/getting-started/> ("Setup From Scratch": `Install-Package
BlishHUD`, and Copy Local = False for the Blish HUD reference and its dependencies).
`GameService.Gw2Mumble` (`IsAvailable`, `CurrentMap.Id`, `PlayerCharacter.Name`) and
`GameService.GameIntegration` (`IsInGame`, the `Gw2Closed` event) were confirmed against the same
package's compiled assembly and XML docs (`Blish HUD.xml` in the NuGet cache) via
`System.Reflection.MetadataLoadContext`, not against a running client — see the "Windows QA" note
below for what that leaves unverified.

## What is verified, and what is not

Verified on this tree (compiled, protocol round-tripped by `tests/ProtocolConsoleTests`):

- The v2 wire contract itself: `hello`/`context`/`heartbeat`/`bye` encode to the exact bytes the
  spec's own example trace shows, and the decoder accepts a well-formed `welcome`/`alert`/`error`
  while tolerating an unknown `type`, a known `type` with an extra or missing key, and a newer `v`.
- The project builds in Release and packages a `.bhm` whose `manifest.json` and DLL match this
  commit (see the delivering agent's report for the exact build log and checksum).

**Not verified — no Windows machine, no running Guild Wars 2 client available here:**

- That `GameService.GameIntegration.IsInGame` combined with `PlayerCharacter.Name` actually produces
  `character_select` vs. `loading` vs. `gameplay` the way `GameContextSampler`'s doc comment
  describes, against a real client going through those screens.
- That the module actually loads inside Blish HUD, connects to a running plugin, shows an alert, and
  sends `bye reason:"game_exit"` when the game process ends.
- A full session with a real player: this is `docs/SPEC-puente-ingame.md`'s own "prueba 14... sesión
  automática de un compañero de principio a fin y aviso visible", still pending QA on Windows.
