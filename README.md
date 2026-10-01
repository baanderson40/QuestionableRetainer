# Questionable Retainer

Questionable Retainer pauses active Questionable automation for ready AutoRetainer work or experimental Ocean Fishing handoffs, chains eligible AutoRetainer and Ocean Fishing work without restarting Questionable between legs, then returns to the original character and resumes the saved quest.

## Requirements

- AutoRetainer with configured characters and Multi Mode travel
- Questionable running in Automatic mode
- Lifestream configured for every registered character and service account
- Optional Ocean Fishing: Henchman, AutoHook, and vnavmesh

When both monitors are enabled, a completed AutoRetainer pass starts an eligible Ocean Fishing window directly, and a completed Ocean Fishing voyage checks for a fresh AutoRetainer pass before returning. Each handoff runs at most one pass and one voyage. If the optional next leg is unavailable, QR skips it and resumes Questionable. Ocean Fishing is skipped after its minute-13 launch cutoff.

## Usage

Open `/questionableretainer`, enable automatic handoffs, and choose whether to monitor retainers, deployables, or Ocean Fishing.

Ocean Fishing uses the Waitingway schedule: registration opens every even UTC hour for 15 minutes. QR defaults to stopping Questionable five minutes before the window and accepts launches through minute 13. AutoRetainer work is never preempted; an active AutoRetainer pass can cause QR to miss the boat.

Ocean Fishing requires Henchman On A Boat in **Single Character** mode with its return/cleanup settings configured, including **Return Once Done** when QR should wait for Henchman to return before cancellation. Henchman AutoRetainer mode is unsupported because it manages AutoRetainer and travel ownership independently. QR confirms the voyage through duty events, waits for results, selling, return travel, normal conditions, no movement, and stable dependencies, then cancels its QR-owned Henchman task. This completion path is experimental because Henchman exposes only global `IsBusy` and no one-voyage completion IPC.

The plugin does not take over an already-running AutoRetainer session. If a handoff is interrupted, the configuration window provides **Recover Handoff** and **Resume Questionable** controls.

After a plugin or game reload, QR never starts Questionable automatically from persisted recovery data. Use **Resume Questionable** or `/qr resume` after confirming the saved handoff is still wanted. Use **Discard Recovery** or `/qr discard` to clear it without starting Questionable.

Questionable's IPC does not expose its automation mode. Single Quest runs cannot be restored exactly and are not supported.
