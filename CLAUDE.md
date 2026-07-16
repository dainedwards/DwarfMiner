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
