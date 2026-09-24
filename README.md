# Questionable Retainer

Questionable Retainer pauses active Questionable automation when AutoRetainer reports ready work, runs one AutoRetainer Multi Mode pass, returns to the original character, and resumes the saved quest.

## Requirements

- AutoRetainer with configured characters and Multi Mode travel
- Questionable running in Automatic mode
- Lifestream configured for every registered character and service account

## Usage

Open `/questionableretainer`, enable automatic handoffs, and choose whether to monitor retainers, deployables, or both.

The plugin does not take over an already-running AutoRetainer session. If a handoff is interrupted, the configuration window provides **Return and Resume** recovery.

Questionable's IPC does not expose its automation mode. Single Quest runs cannot be restored exactly and are not supported.
