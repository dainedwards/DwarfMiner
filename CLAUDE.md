# DwarfMiner — notes for Claude sessions

## Run command for testing (machine-parsed — keep the exact format)

Whenever you set up a worktree, finish a change, or the way to test changes, print the
run command as a single line in exactly this shape:

    cd <absolute checkout path> && DM_VAR=value DM_OTHER=1 dotnet bin/<Config>/net8.0/DwarfMiner.dll

Rules:

- Default to `DM_GOD=1 DM_FLY=1 DM_AUTOSTART=debug` when applicable — i.e. unless the
  change specifically needs death, normal movement, or a different start scenario to
  be tested (then drop or replace only the conflicting variable).
- Additionally include every other `DM_*` environment variable needed to test the
  current change.
- When you add a new `DM_*` test variable — or the right set of variables changes —
  print the updated command again in the same format. The newest printed command wins.
- Keep it to one line: absolute `cd` path first, then the `DM_*` assignments, then
  `dotnet …DwarfMiner.dll` last. The user's `run` launcher greps session transcripts
  for the newest line matching this format and executes it verbatim, so a command in
  any other shape makes the change untestable from the user's side.

## Game window title (identifies which tree is running)

The game window title must be `DwarfMiner - <worktree>` (e.g. `DwarfMiner - liquid-perf`,
`DwarfMiner - main`) so the user can tell test builds apart.

- The game reads the `DM_TITLE` environment variable: when it is set, set the window
  title to `DwarfMiner - <value>` (in Game1's Initialize, via `Window.Title`). If the
  branch you are on does not support `DM_TITLE` yet, add it as part of your change.
- Always include `DM_TITLE=<worktree name>` (`DM_TITLE=main` on the main checkout) in
  the run commands you print. The user's launcher also injects it when missing, but
  printed commands should carry it explicitly.
