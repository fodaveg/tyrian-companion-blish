# Tyrian Companion Bridge (Blish HUD)

A Blish HUD module that mirrors the loot and price alerts the "Tyrian Companion" Obsidian plugin
emits (`valuable_loot`, `always_alert`, `sell_signal`, `hold_signal`) onto a Guild Wars 2 screen
notification, so they are visible while the game has focus.

This is the Blish HUD half of `docs/SPEC-puente-ingame.md`'s M4 (the plugin's own repo carries the
spec and the server side; this repo only carries the game-side client). The Nexus/Raidcore addon
that covers the same alerts under `tyrian-companion-nexus` is a separate repo and a separate
codebase — the two do not share a binary, only the wire contract below.

## What it does, and does not, do

- Connects to the plugin over loopback TCP (`127.0.0.1`, port `47823` by default, configurable in
  the module's own settings, and it has to match the port set in the plugin's settings).
- Sends exactly one line on connect — its `hello` — and never writes to the socket again.
- Reconnects forever on a saturated backoff (`250, 500, 1000, 2000, 5000` ms), silently: no server
  listening yet is the expected state if Blish HUD starts before Obsidian does.
- Paints the `content` string each alert line carries verbatim, through
  `ScreenNotification.ShowNotification`. It does not recompose the message from the other fields
  the wire line carries (`name`, `quantity`, `totalCopper`); `kind` only picks a notification color.
- Does **not** call the Guild Wars 2 API, with or without a key.
- Does **not** read Mumble Link, game memory, inventory, or position, even though Blish HUD exposes
  Mumble Link to every module.
- Does **not** automate any action inside the game.
- Sends nothing to the plugin beyond the one `hello` line above, ever.

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

## Installing

Copy the built `TyrianCompanionBlishBridge.bhm` into:

```
Documents\Guild Wars 2\addons\blishhud\modules
```

Then enable the module from Blish HUD's own module list, and enable "Show in-game alerts" and check
the "Plugin port" in the module's settings (defaults to `47823`, matching the plugin's own default —
if either side's port was changed from its default, both have to agree).

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

None of the above is part of this module's own QA. `docs/SPEC-puente-ingame.md`'s own risk list
treats a client that cannot be verified on the primary platform as non-blocking for the rest of the
in-game bridge lot: the Nexus/Raidcore addon in the sibling repo is the one verified on Linux.

## Source of the API used here

Written against the `BlishHUD` NuGet package `1.3.0`
(<https://www.nuget.org/packages/BlishHUD/1.3.0>) and the `dev` branch of
<https://github.com/blish-hud/Blish-HUD> at the time of writing, plus the official docs at
<https://blishhud.com/docs/modules/overview/bhm/> (the `.bhm` package format) and
<https://blishhud.com/docs/modules/overview/getting-started/> ("Setup From Scratch": `Install-Package
BlishHUD`, and Copy Local = False for the Blish HUD reference and its dependencies). Not compiled;
see this repo's commit history / the delivering agent's report for exactly what was and was not
verified.
