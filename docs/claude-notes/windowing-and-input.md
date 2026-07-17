# Windowing, focus and input

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

## DM_NOFOCUS: how the invisible test launch works

All four parts are load-bearing (`Program.cs`, `Game1.HideWindowEarly`/`KeepWindowHidden`/
`MakeWindowUntouchable`):

1. **`SDL_MAC_BACKGROUND_APP=1`**, poked into the real environment via `libc setenv` before SDL
   starts, so SDL skips `activateIgnoringOtherApps`: no focus theft, no dock icon. .NET's
   `Environment.SetEnvironmentVariable` writes a MANAGED copy only — SDL's native `getenv`
   never sees it, which is why the P/Invoke is needed.
2. **`SDL_SetWindowOpacity(handle, 0)` in `Initialize`** — this is what kills the startup
   FLASH. Opacity is independent of shown/hidden and survives `SDL_ShowWindow`.
3. **`SDL_HideWindow` re-armed EVERY frame from `Update`.** MonoGame's run loop calls
   `Sdl.Window.Show` itself when the loop starts (measured ~583ms), which is ~25ms AFTER the
   first Update — XNA semantics run one Update before the window is shown. A one-shot hide in
   `Initialize` OR in the first `Update` is silently undone and the window is back for the
   whole session.

4. **`MakeWindowUntouchable` (objc P/Invoke in `Initialize`)** — activation policy →
   `Prohibited` and `ignoresMouseEvents` → YES on the NSWindow. Parts 1–3 only make the window
   *invisible*; they never stopped it taking INPUT. The gap: MonoGame's loop-start
   `SDL_ShowWindow` leaves the transparent window on screen until the next `Update` re-hides
   it, and an early frame stalled on worldgen/prewarm stretches that gap to seconds — a click
   landing there activated the app and moved the user's keyboard focus into an invisible
   window (reported live 2026-07-16: "the hidden window is taking my input"). Prohibited means
   macOS refuses to activate the app at all; ignoresMouseEvents lets clicks fall through even
   while shown. Verified: window-server probe shows policy 2, zero visible windows, and
   DM_AUTOSHOT captures unaffected.

Belt-and-braces on top: the `UpdateFrame` input gate (below) is forced shut for the entire
life of a `_noFocus` run, so even if SDL reports the window focused the game never plays from
the user's keys.

`Window.Handle` is the SDL_Window* and is stable throughout (logged: it never changes).
`DllImport("libSDL2")` resolves — the dylib ships under
`bin/<Config>/net8.0/runtimes/osx/native/` and deps.json puts it on the search path. MonoGame
doesn't bind Hide/Opacity itself.

`KeepWindowHidden` checks `SDL_GetWindowFlags & SDL_WINDOW_HIDDEN` and warns once on failure:
with no dock icon, a window that fails to hide cannot be minimised, hidden or quit. Believe
that warning — it means the mechanism broke.

**Half-measures made it WORSE, and the user felt it.** The accessory policy alone still
order-fronts the window: it sat ON TOP, took input when clicked, and with no dock icon there
was no way to manage it. Hiding alone would still let SDL activate the app on launch.

**Trap:** an ASCII `strings` grep over `MonoGame.Framework.dll` proves nothing about which SDL
calls MonoGame binds — .NET metadata is UTF-16, so it reads as a false negative.

## Verifying a window is really hidden — use the window server, not a screenshot

This wasted a lot of time twice: `screencapture` only sees the CURRENT SPACE. Rider sits
fullscreen in its own Space, so a test window living on the desktop Space is absent from the
capture — twice that was read as "hidden" while the window was plainly on screen.

- **Ground truth**: `swift` + `CGWindowListCopyWindowInfo(.optionAll)`, filtered by
  `kCGWindowOwnerPID` and `Height > 100`, checking `kCGWindowIsOnscreen && kCGWindowAlpha > 0`.
  Poll ~66Hz to catch a one-frame flash.
- **ALWAYS run the control** without `DM_NOFOCUS` — it must report the window visible. Without
  that A/B, "never visible" may just mean the detector is blind.
- **Filter by pid.** Both Claude sessions' games run as owner `dotnet` with the same window
  title, and Rider owns a window literally named "DwarfMiner".
- A visible window during your test is often the USER's own `run` playtest (Release build) —
  check `pgrep -fl DwarfMiner.dll` for a second pid before blaming your own launch, and never
  kill theirs.

## Unfocused input gate

The game ignores keyboard and mouse whenever the window isn't focused (`IsActive` gate at the
top of `UpdateFrame`): SDL reports the OS-wide keyboard/pointer even when the window is not
key, so without it, typing in another app drives the player.

While blurred the input reads as a neutral keyboard and a frozen mouse (buttons up, position
and wheel held at their last focused values — passing the live pointer through would drag menu
hover and reticle aim around from another app).

On the first focused frame the previous-state baseline is adopted from the REAL state before
anything reads it, so the click that refocuses the window isn't also a trigger pull and a
scroll elsewhere isn't a hotbar spin. The alternative (prev=real, cur=neutral) fires spurious
RELEASE edges, which is worse — that's what would trip the charge-throw gauge.

Every input path flows through those two locals (the only `GetState` calls outside
`Screen.Mouse()`), so the one gate covers everything.

## Window title internals

`Game1.TitleBar()` + `LocateTree()` + `BranchAt()` walk up from `AppContext.BaseDirectory` (the
binary sits at `bin/<Config>/net8.0/` INSIDE the checkout) to the `.git` entry and read git's
plain files — no shelling out to `git`, so a slow or missing binary can't stall startup.

A linked worktree (`.git` FILE) reports its folder name; the primary checkout (`.git`
DIRECTORY) reports `main`, because its folder is named after the repo and would otherwise read
the redundant `DwarfMiner | noita-sim | DwarfMiner`.

**Why self-derived:** the user wanted every build tellable apart with nothing to remember to
set; the old launcher-injected `DM_TITLE` silently produced an untitled window whenever it went
missing. DM_TITLE is gone from the code — never print it in a run command.

Accessibility window enumeration via System Events returns EMPTY here (no permission), so read
a title off a `screencapture -x` frame, or better, use the window-server method above.
