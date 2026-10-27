# Monitor & Audio Switcher

*A tiny Windows tray app for people whose PC is in one room and whose couch is in another.*

## The problem (a.k.a. my life)

My gaming PC lives in room 1. My TV lives in room 2, on the other side of the house. When I want to
game on the couch, my routine goes like this:

1. Walk to room 1, turn on the PC, log in.
2. Walk to room 2, press the button on the controller, Steam Big Picture opens on the TV. 🎮
3. Game.
4. When I'm done, pick **Turn off** in Big Picture, because I'm absolutely not walking back to room 1
   to shut down manually.

Steps 1–4 are fine. The problems:

- While I'm gaming, my desk monitor in room 1 is sitting there glowing at an empty room. That's a
  waste of electricity and of my monitor's lifespan.
- If I turn the desk monitor off in Windows, it **stays off**. Next time I boot the PC, Windows
  happily shows everything on the TV in the other room. So I'm in room 1 staring at a black monitor,
  playing "guess where the mouse cursor is" and running to the TV every few seconds to check. Got so fed up and now here we are.
- The audio has to follow too, or the game plays sound to my desk speakers while I'm in another room.
- Some monitor-switching tools also quietly drop your nice 144/165 Hz monitor back to **60 Hz** when
  they turn it back on. Ask me how I know.

## What this does

It sits in your tray and watches for **Steam Big Picture** (or any window/app you choose).

| When… | It does… |
| --- | --- |
| Big Picture opens | Switches to your **TV setup**: TV on, desk monitor off, audio to the TV |
| Big Picture closes | Switches back to your **Desk setup**: monitor on, TV off, audio to your speakers |
| Windows shuts down (even from Big Picture's *Turn off*) | Restores the **Desk setup** first, so the next boot and the sign-in screen show up on your desk monitor |
| The app starts at sign-in and Big Picture isn't open | Double-checks the Desk setup is active, just in case |

Also:

- **Keeps your refresh rate.** It saves the *exact* display setup (which screens, resolution, refresh
  rate, position) and restores it with the same Windows feature Win+P uses. 144 Hz stays 144 Hz.
- **Fast.** Capturing a layout takes a few milliseconds, so no "freeze the PC for 5 seconds" moments.
- **Doesn't fight you.** It only reacts when Big Picture opens or closes. If you switch something
  yourself, it leaves you alone.
- Optional global **hotkeys** for each setup, which are great for switching back to your monitor
  without being able to see anything.
- An activity log, so when something weird happens you can see what Windows actually said.

## Is this for you?

**Probably yes if:**

- You have a PC + monitor in one place and a TV somewhere else (or just want the TV to be the only
  screen while gaming).
- You use Steam Big Picture, or any app with a recognizable window title or `.exe` name.
- You're tired of Win+P roulette. THIS.

**Probably not if:**

- You want *both* screens on at the same time. Windows already does that, no app needed.
- You're not on Windows 10/11.
- Your TV fully disconnects from HDMI when it's off. Windows can't turn on a screen it can't see.
  Most TVs are fine, but some go *really* deep to sleep. Turn the TV on first.

## Getting started

**Requirements:** Windows 10/11 (x64).

1. Download an exe from the [Releases](../../releases/latest) page and put it somewhere permanent
   (the startup entry points at wherever the exe lives):
   - `MonitorAudioSwitcher-standalone.exe`: just run it, nothing else to install. It's a chunky
     ~85 MB because it brings its own .NET.
   - `MonitorAudioSwitcher.exe`: a slim ~7.5 MB, but needs the
     [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) installed.
     If you don't have it, Windows will offer to install it.
2. Run it. It opens a window and adds a tray icon.
3. **Capture your Desk setup:** set Windows up how you like it at the desk (monitor on, TV off,
   refresh rate set in *Settings → Display → Advanced display*). Then hit **Capture current layout**
   on the *Desk setup* card.
4. **Capture your TV setup:** switch to TV only (Win+P → *Second screen only*), check the TV's
   refresh rate, and hit **Capture current layout** on the *TV setup* card. Then switch back.
5. Pick an **audio output** for each setup.
6. Leave **Auto-switch** and **Run when I sign in to Windows** turned on.
7. Test the **Switch to TV now** and **Switch to Desk now** buttons once, ideally while someone else
   checks the TV so you don't have to do the hallway sprint.

Closing the window just hides it to the tray. Use **Exit app** or the tray menu to actually quit it.

Your settings live in `%APPDATA%\MonitorAudioSwitcher\config.json`, and the log is `switcher.log`
in the same folder.

## Troubleshooting

- **Nothing switches:** check the *Trigger* setting matches the window title (default:
  `Steam Big Picture Mode`). You can also use a process name like `game.exe`.
- **Wrong refresh rate:** re-capture that setup after setting the refresh rate in Windows. The app
  restores exactly what it captured.
- **The TV audio device says "not connected":** that's normal while the TV is off. The app waits a few
  seconds for HDMI audio to show up after switching.
- **Something else went wrong:** open the Activity log (there's a button for it) and see what Windows
  said.
- **Stuck staring at a black monitor anyway:** that's what the hotkeys are for. Set one for Desk.

## Building from source

```bash
cd src/MonitorAudioSwitcher
dotnet publish -c Release -o ../../dist
```

Needs the .NET 10 SDK. You get a single `dist/MonitorAudioSwitcher.exe`. Add
`-p:SelfContained=true -p:EnableCompressionInSingleFile=true` for the standalone version.

Releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)).
Push a version tag and both exes get attached to a new release:

```bash
git tag v2.0.1
git push origin v2.0.1
```

Built with C# / WPF and [WPF-UI](https://github.com/lepoco/wpfui) for the Windows 11 look. Displays
are switched with the Windows display configuration API (`SetDisplayConfig`), and audio with the
Core Audio APIs.

## Disclaimer

This changes your display setup on purpose. It's been tested on exactly one PC (mine, with an MSI
monitor and a TV across the house). If it does something weird on yours, Win+P is your friend, and
issues/PRs are welcome.
