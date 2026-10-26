# RaCMAN Reloaded: user guide

The [README](README.md) is the quickstart. This guide has the details.

## Downloads

Download the newest release from the [releases page](https://github.com/sowelilili/RaCMAN-Reloaded/releases). Each release contains the client, the console module `qwark.sprx`, the console menu's file `ui.pak`, the RPCS3 helper and a console installer.

- **Windows, installer**: run `RaCMANReloaded-win-Setup.exe`. It installs for your user account only. It does not need administrator rights.
- **Windows, portable**: unzip `RaCMANReloaded-win-Portable.zip` and run `RaCMAN.App.exe`.
- **Linux**: download `RaCMANReloaded-linux.AppImage`, make it executable and run it.

**Install without a PC**: download `qwark-installer.pkg` and install it on the console with Package Manager. Run **qwark installer** once. It installs the module and restarts the console.

## Connect to a PS3

1. Start the console and make sure webMAN runs on it.
2. Type the console's IP address into the client.
3. Press **Connect**.
4. Start one of the four games.

The client finds the module on the console, sends it if no plugin slot holds it, and connects. Each step shows a message. If a step fails, the message tells you which step failed.

The panels fill with the controls for the game that runs. If the connection stops, the client connects again by itself. The console keeps the state, so nothing is lost.

If you start a game the module does not know, the client keeps the Memory panel and hides the rest: watches, freezes, patches and the memory viewer work on any title.

The Connection panel shows how much memory the console's system software (the VSH) has free, and the lowest it has been since the module started. If the console crashes or says it is out of memory, include that line when you report it. Below 512 KB free, the status line also shows **low memory**.

## Connection modes

The mode is on the Settings panel, in the **Connection** section.

**webMAN** is the default mode. The client asks webMAN which plugin slot holds the module. If no slot holds it, the client sends `qwark.sprx` to the console and loads it. Automatic reconnections do the same, so the client can recover a module that stopped.

**Standalone** never uses webMAN. The client only connects to the module's port. Use this mode if the console loads the module at startup.

Standalone mode adds an **Installation** part to the Connection panel. It makes the console load the module at startup. Press **Install to boot_plugins.txt**, then restart the console. The console reads that list only at startup. The install puts qwark first on the list, so HEN loads it before webMAN. This one installation needs webMAN's FTP server. After it, the client does not need webMAN.

The client sends `qwark.sprx` and `ui.pak` by itself. To copy them by hand, put `qwark.sprx` in `/dev_hdd0/plugins/` and make `/dev_hdd0/plugins/qwark.sprx` the first line of `/dev_hdd0/boot_plugins.txt`. Put `ui.pak` in `/dev_hdd0/qwark/`.

In standalone mode the client also keeps that copy current. If the console runs an older module than the client ships, the client sends the new one to the console and asks you to restart it. See [docs/standalone-update.md](docs/standalone-update.md).

## Connect to RPCS3

1. In RPCS3, switch the IPC server on. It is in Manage, Network Services, IPC.
2. Start the game.
3. In the client, select **RPCS3** on the Connection panel.

The PS3 and RPCS3 keep separate settings. A switch to RPCS3 does not change the console's address. Each one has its own **Reconnect automatically** box.

The client starts `qwark-rpcs3.exe` beside it and connects to it on this PC. The panels behave as they do on a console, with one difference: RPCS3 recompiles the game code, so it cannot accept code patches while the game runs. Code patches go in as RPCS3 patches instead, and RPCS3 applies them when the game starts. Everything that writes data works without them.

Mods work under RPCS3 as RPCS3 patches: tick **Enabled** for a mod on the Mods panel, then restart the game in RPCS3.

To save and load save files under RPCS3, install qwark's patches for each game. Press **Install qwark patches...** in the RPCS3 part of the Connection panel, then restart the game in RPCS3. The client asks before it writes to RPCS3's folder.

The cheats that patch game code, such as fast loads and infinite ammo, work after **Install qwark patches...** and a game restart.

RPCS3 accepts one IPC client at a time. If another program holds the port, the client says so and waits for the port. RPCS3 support is for Windows at the moment.

## Manip categories

Open **Game**, then **Manips**, and pick the category type of your run. Hover a type to see its leaderboard categories. The console applies the manips at the start of a run, also when no PC is connected. In the first three games, a run starts when a save or a new game loads the first planet. In Deadlocked, a run starts with a new game or a challenge-mode start. **Auto-reset** applies them again at every run start, and **Remember category** keeps your choice when the game restarts. To practise from a save on another planet, press **Apply now**. NG+ manips need a challenge-mode save, and the other manips need a new save. If the save does not match, the console writes nothing and the client tells you.

The Save files panel has premade saves in its last category, **(premade savefiles)**. You can load them, but you cannot change them.

## Console menu

On a console, all four games have a Trainer menu on the TV. Open it from **Trainer** in the pause menu, where Help was, or press L1, R1, L2, R2, L3 and R3 together in play. The Combos panel can change that combo. Untick **In-game menus** there to keep the game's own Help in the pause menu; the combo still opens the Trainer menu.

The Combos panel also has a **Toggle fast loads** combo. It has no buttons until you capture some, and the console says whether fast loads are now on or off.

If you change or clear the console menu combo, hover its row to see the default buttons, and capture them again to put it back.

## Per-user settings

A PS3 can keep separate settings for each user on the console. Each user gets their own position slots, the mods and cheats that load automatically, manip and planet choices, and the **In-game menus** setting. The mod and savefile libraries stay shared.

Turn it on with **Per-user settings** under **Console** on the Settings panel, while you are connected. If the signed-in user has no settings of their own yet, the client asks whether to copy the current settings or start from the defaults. Users who sign in later start from the defaults. To change user, sign in as someone else on the PS3 home screen; the line under the switch says whose settings you are editing.

Combos are shared by everyone, and so is the **Combos enabled** switch. To give the signed-in user their own combo for one action, press **Override** on that row and press the buttons. That row then shows in orange. **Clear** on an orange row removes only the override, so the shared combo comes back; press **Clear** again to clear the shared combo. Overrides are kept while per-user settings are off.

RPCS3 has a single user, so this setting does not show there.

## Mods on a USB drive

A PS3 can load mods from a USB drive. Put each mod folder in `qwark/mods/<TITLEID>/` or in `mods/<TITLEID>/` on the drive, for example `mods/NPEA00423/il-ghost/`. Any USB port works.

The Mods panel lists these mods as **On USB000** (or the port they are on). Nothing is installed until you press **Load** or tick **Auto**: then the console copies the mod from the drive and keeps it.

When the drive has a newer version of a mod than the console, the Console column says **update ready**, and the console takes it the next time the mod loads. Hover over the cell to see the USB version, the console's version and the path on the drive. A mod that is loaded right now keeps its files until it loads again. The console compares the `#- version` line in each `patch.txt`, so give each new release a higher number. An older copy, or one with the same version, never replaces the console's copy. If the same mod is on more than one drive or folder, the console uses the newest. A copy with the console's version but different contents is not flagged in the column: the mod just says **ready**, and its tooltip warns that the console uses its own copy.

**Load** also uses the console's copy when it is newer than the one in your mods folder, and does not upload yours over it. Under RPCS3 there are no USB mods: use the mods folder on your PC.

## Chargeboot colours

In Going Commando and Up Your Arsenal, **Game**, then **Cosmetics**, has three chargeboot colour pickers: **primary front**, **primary back** and **tint**. They work on a console and under RPCS3.

**Presets** above the pickers holds your saved colour sets. Type a name and press **Save** to keep the colours you changed, and pick a preset to put them back. Pick **(none)** to put the game's own chargeboot colours back.

The colours stay until the game restarts. Tick **on boot** beside **Save** to have the console set them again every time the game boots, also with no PC connected. While the box is ticked, the console also keeps every colour you change. With per-user settings on, each user has their own.

## Deadlocked's HUD colour

In Deadlocked, **Game**, then **Cosmetics**, has a **HUD** heading with a **HUD colour** picker. It sets any colour for the HUD, where the game's HUD colour cheat offers only a few. The whole HUD changes at once: the health bar, the EXP bars, the ammo, the quick-select slots, the radar and its outline, the prompt and pickup boxes, the bot HUD and the end screen.

The HUD colour is qwark's own code in the game, so it works on a console as soon as you connect. Under RPCS3 it needs qwark's patches: press **Install qwark patches...** on the Connection panel, then restart the game in RPCS3. Until then the HUD block is greyed out, and its pickers show the game's own colours.

The game draws the HUD in that colour and three shades of it: **vivid** (brighter: the health bar, the ammo, the radar's cursor), **dark** (the bottom of the HUD's gradients) and **light** (the top of the prompt and pickup boxes, and the radar cursor's pulse). You don't have to set them. The pickers under **HUD colour** are faded while the console works them out from the HUD colour, and they follow it when you change it. Pick a colour in one to override that shade. It then stays as you set it when the HUD colour changes, and an **Auto** button beside it hands it back to the HUD colour.

**Brightness.** The PS3 version draws its HUD twice as bright as the game's own colours say, the game's own HUD too. Two boxes under the pickers make up for it, each for its own part of the HUD:

- **Correct gradient brightness**: the health bar and the two EXP bars.
- **Correct HUD brightness**: everything else: the radar and its outline, the quick-select slots (the d-pad), the prompt and pickup boxes, the end screen and the bot HUD.

A ticked box draws its part as bright as the game meant it, so it shows exactly the colour you picked (an odd number can come out one higher). An unticked box keeps the brighter look of the PS3 version. So to keep the bright health and EXP bars without a washed-out radar, tick **Correct HUD brightness** only. The boxes change the colours the HUD draws, not the colours you picked: the pickers show the same either way.

Below a line, **Weapon colour V11**, **Weapon colour V99** and **Gadget colour** set the colours of the weapon icons. The game colours each weapon's icon by its level: grey up to V10, then from blue at V11 to purple at V99. In multiplayer, every level is blue. V11 and V99 set the two ends of that gradient, and **Gadget colour** the colour of an item that is no weapon. They are separate from the HUD colour, and the brightness boxes don't change them: the icons are drawn at the brightness the game meant already.

The HUD colour is your HUD's own. It doesn't change the game's team colours, so the HUD colour cheat's preview, the vehicle health bar, the menus and multiplayer keep their colours. While a colour is set, it is the HUD's colour whatever the HUD colour cheat says. Pick **(none)** to go back to the game's own, cheat included. In multiplayer the HUD keeps the team colours.

**Presets** above the pickers works as for the chargeboots, and a preset is the whole look: the HUD colour, the shades you overrode, the weapon colours you picked, and both brightness boxes. Picking it puts all of that back, and leaves the colours it doesn't name as they are. Pick **(none)** to put the game's own look back: the game's colours, and both brightness boxes unticked.

The colours and the boxes stay until the game restarts. Tick **on boot** beside **Save** to have the console set all of them again every time the game boots, also with no PC connected. While it is ticked, a change to a colour or a box is kept too.

## Updates

The client asks GitHub for a newer release once a day. If there is one, a bar appears at the top of the window. Press **Download**, then **Restart to update**. The client downloads nothing until you press the button.

**What's new** on the bar shows the release notes. The first start after an update offers them once more.

The Settings panel holds the switch, a **Check now** button and the version you run. The installer and the portable build can update themselves. A folder that you built yourself cannot.

A new client can carry a newer console module. The console keeps the module it already loaded, so the header says that `qwark.sprx` is out of date. The Connection panel then tells you how to send the new one. In standalone mode the client sends it by itself.

## Your files

Your settings, colour presets, watchlists, save files and mods are in the data folder. An update never touches it.

| | |
|---|---|
| Windows | `%APPDATA%\RaCMAN Reloaded` |
| Linux | `$XDG_CONFIG_HOME/racman-reloaded`, or `~/.config/racman-reloaded` |
| macOS | `~/Library/Application Support/RaCMAN Reloaded` |

The Settings panel has an **Open folder** button for it. To use a different folder, start the client with `--data-dir <path>`, or set the `RACMAN_DATA_DIR` variable. A copy on a memory stick needs one of the two.

You can put your own mods in `mods/<TITLEID>/` in that folder. A mod of yours replaces a shipped mod with the same folder name. To remove a mod from the PC and the console, right-click it on the Mods panel and pick **Uninstall...**.

If you come from the old RaCMAN, **Import from RaCMAN** on the Settings panel copies its save files and its mods into that folder as well as its settings, and it replaces nothing you already have. A few of the old default mods are left behind, and the import says which and why. See [docs/import.md](docs/import.md).

## Windows firewall

The console sends live data to the client over UDP. Windows blocks that data for an unsigned application, so the client offers to add a firewall rule for itself. If you refuse, the client uses TCP instead, which is slower, and the status line shows **TCP fallback**. The Connection panel keeps a button to add the rule later.

The client checks for its rule at every start. It asks again only if the rule is missing. See [docs/firewall.md](docs/firewall.md) if the rule does not seem to take.

## The input display in OBS

The client can serve the input display as a page for OBS. Add a Browser Source with the URL on the Input display panel, and give it the size the panel shows. The page is served on 127.0.0.1 only, so it needs no firewall rule. See [docs/obs.md](docs/obs.md).

## Customising the Game page

The client, not the console, decides where each control is drawn. `data/gamelayout.json` holds that layout. You can move a feature to another section, and make a section into a sub-page or a tab. See [docs/gamelayout.md](docs/gamelayout.md).
