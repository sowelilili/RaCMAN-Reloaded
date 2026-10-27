# Features in test builds only

These features are in qwark's test builds, and release builds leave them out until they are finished (qwark's docs/PROTOCOL.md 3.1.2 and 9.11). Their text moves back into [USER-GUIDE.md](../USER-GUIDE.md) when a release ships them. With per-user settings on, each user also keeps their own chargeboot and HUD colours for the boot.

## Per-user settings

A PS3 can keep separate settings for each user on the console. Each user gets their own position slots, the mods and cheats that load automatically, manip and planet choices, and the **In-game menus** setting. The mod and savefile libraries stay shared.

Turn it on with **Per-user settings** under **Console** on the Settings panel, while you are connected. If the signed-in user has no settings of their own yet, the client asks whether to copy the current settings or start from the defaults. Users who sign in later start from the defaults. To change user, sign in as someone else on the PS3 home screen; the line under the switch says whose settings you are editing.

Combos are shared by everyone, and so is the **Combos enabled** switch. To give the signed-in user their own combo for one action, press **Override** on that row and press the buttons. That row then shows in orange, and its buttons read **Override**, which captures the override again, and **Clear Override**, which removes only the override, so the shared combo comes back; press **Clear** again to clear the shared combo. Overrides are kept while per-user settings are off.

RPCS3 has a single user, so this setting does not show there.

## Mods on a USB drive

A PS3 can load mods from a USB drive. Put each mod folder in `qwark/mods/<TITLEID>/` or in `mods/<TITLEID>/` on the drive, for example `mods/NPEA00423/il-ghost/`. Any USB port works.

The Mods panel lists these mods as **On USB000** (or the port they are on). Nothing is installed until you press **Load** or tick **Auto**: then the console copies the mod from the drive and keeps it.

When the drive has a newer version of a mod than the console, the Console column says **update ready**, and the console takes it the next time the mod loads. Hover over the cell to see the USB version, the console's version and the path on the drive. A mod that is loaded right now keeps its files until it loads again. The console compares the `#- version` line in each `patch.txt`, so give each new release a higher number. An older copy, or one with the same version, never replaces the console's copy. If the same mod is on more than one drive or folder, the console uses the newest. A copy with the console's version but different contents is not flagged in the column: the mod just says **ready**, and its tooltip warns that the console uses its own copy.

**Load** also uses the console's copy when it is newer than the one in your mods folder, and does not upload yours over it. Under RPCS3 there are no USB mods: use the mods folder on your PC.

## Game modules

In a test build, each game's support is a file of its own, named for the game: `rac1.sprx` (RaC1), `rac2.sprx` (RaC2), `rac3.sprx` (UYA), `rac4.sprx` (Deadlocked) and `tod.sprx` (Tools of Destruction). They sit beside `qwark.sprx` and `ui.pak`, and the client sends them wherever it sends `ui.pak`: the webMAN load, **Install to boot_plugins.txt** and the standalone update. To copy them by hand, put them in `/dev_hdd0/qwark/` beside `ui.pak`.

Your console loads a game's file when that game starts and it has the memory for it. Until then the game has no game features: the status line names the game, the Memory panel works, and the Game panel says why.

- **Waiting for memory**: your console has too little free memory right now. The status line says "loading...", and its tooltip and the Game panel show how much memory your console has and how much it needs. The game support loads by itself when there is room. If it waits more than 30 seconds, your console shows a notification with the same figures.
- **Missing, damaged or from another build**: the status line shows it in red, and the client tells you once. Press **Send rac1.sprx** (or the file for your game) on the Connection panel, the Game panel or the status line. Your console loads it without a restart.

A release links the games in and has no game module files, so none of this shows.
