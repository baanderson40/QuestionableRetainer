# Questionable Retainer

Questionable Retainer pauses active Questionable automation for ready AutoRetainer work or experimental Ocean Fishing handoffs, chains eligible AutoRetainer and Ocean Fishing work without restarting Questionable between legs, then returns to the original character and resumes the saved quest.

## Requirements

- AutoRetainer with configured characters and Multi Mode travel
- Questionable running in Automatic mode
- Lifestream configured for every registered character and service account
- Optional Ocean Fishing: WahTools Feesh and Lifestream

When both monitors are enabled, a completed AutoRetainer pass starts an eligible Ocean Fishing window directly, and a completed Ocean Fishing voyage checks for a fresh AutoRetainer pass before returning. Each handoff runs at most one pass and one voyage. If the optional next leg is unavailable, QR skips it and resumes Questionable. Ocean Fishing is skipped after its minute-13 launch cutoff.

## Usage

Open `/questionableretainer`, enable automatic handoffs, and choose whether to monitor retainers, deployables, or Ocean Fishing.

Ocean Fishing uses the Waitingway schedule: registration opens every even UTC hour for 15 minutes. QR defaults to stopping Questionable five minutes before the window and accepts launches through minute 13. AutoRetainer work is never preempted; an active AutoRetainer pass can cause QR to miss the boat.

Ocean Fishing uses WahTools Feesh IPCs. QR supports the current character or one selected AutoRetainer character per window, switches through Lifestream, disables WahTools repeat voyages, and requests one trip. QR does not restore the previous repeat-voyage setting. QR observes territory, duty events, and safe post-voyage conditions; one window never starts more than one attempt. The selected character is locked when QR claims the window. CleanBags and inventory monitoring are not managed by QR.

The plugin does not take over an already-running AutoRetainer session. If a handoff is interrupted, the configuration window provides **Recover Handoff** and **Resume Questionable** controls.

After a plugin or game reload, QR never starts Questionable automatically from persisted recovery data. Use **Resume Questionable** or `/qr resume` after confirming the saved handoff is still wanted. Use **Discard Recovery** or `/qr discard` to clear it without starting Questionable.

Questionable's IPC does not expose its automation mode. Single Quest runs cannot be restored exactly and are not supported.
