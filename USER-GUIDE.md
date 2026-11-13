# RaCMAN Reloaded: user guide

The [README](README.md) is the quickstart. This guide has the details.

## Downloads

Download the newest release from the [releases page](https://github.com/sowelilili/RaCMAN-Reloaded/releases). Each release contains the client, the console module `qwark.sprx`, `ui.pak` (the file the console menu and save files need), the game files `rac1.sprx` to `rac4.sprx` and `tod.sprx` (see [Game modules](#game-modules)), the RPCS3 helper and a console installer.

- **Windows, installer**: run `RaCMANReloaded-win-Setup.exe`. It installs for your user account only. It does not need administrator rights.
- **Windows, portable**: unzip `RaCMANReloaded-win-Portable.zip` and run `RaCMAN.App.exe`.
- **Linux**: download `RaCMANReloaded-linux.AppImage`, make it executable and run it.

**Install without a PC**: download `qwark-installer.pkg` and install it on the console with Package Manager. Run **qwark installer** once. It installs the module and restarts the console.

The console installer also adds the mods and premade savefiles that come with the client, so your console has them without a PC: the mods appear on the Trainer menu's **Mods** page and the premade saves in the save files menu's **(premade savefiles)** category. It only adds what your console does not have yet. A mod or save file that is already on your console, whether the client sent it or you changed it, is skipped and stays as it is, and the installer tells you how many it skipped. To get a fresh copy of one, delete yours first.

## Connect to a PS3

1. Start the console and make sure webMAN runs on it.
2. Type the console's IP address into the client.
3. Press **Connect**.
4. Start one of the games: the four original games or Tools of Destruction.

The client finds the module on the console, sends it if no plugin slot holds it, and connects. Each step shows a message. If a step fails, the message tells you which step failed.

The panels fill with the controls for the game that runs. If the connection stops, the client connects again by itself. The console keeps the state, so nothing is lost.

If you start a game the module does not know, the client keeps the Memory panel and hides the rest: watches, freezes, patches and the memory viewer work on any title.

The Connection panel shows how much memory the console's system software (the VSH) has free, and the lowest it has been since the module started. If the console crashes or says it is out of memory, include that line when you report it. Below 512 KB free, the status line also shows **low memory**.

## Connection modes

The mode is on the Settings panel, in the **Connection** section.

**webMAN** is the default mode. The client asks webMAN which plugin slot holds the module. If no slot holds it, the client sends `qwark.sprx` to the console and loads it. Automatic reconnections do the same, so the client can recover a module that stopped.

If your console also loads qwark at startup (`boot_plugins.txt` lists `qwark.sprx`), a webMAN load replaces that copy too, so a restart starts the same build. The old copy is kept as `qwark.sprx.old`. If the client can't replace it, it says so; the old copy stays, and a restart loads it. A console whose list doesn't name qwark is never added to it.

**Standalone** never uses webMAN. The client only connects to the module's port. Use this mode if the console loads the module at startup.

Standalone mode adds an **Installation** part to the Connection panel. It makes the console load the module at startup. Press **Install to boot_plugins.txt**, then restart the console. The console reads that list only at startup. The install puts qwark first on the list, so HEN loads it before webMAN. This one installation needs webMAN's FTP server. After it, the client does not need webMAN.

The client sends `qwark.sprx`, `ui.pak` and the game files by itself. To copy them by hand, put `qwark.sprx` in `/dev_hdd0/plugins/` and make `/dev_hdd0/plugins/qwark.sprx` the first line of `/dev_hdd0/boot_plugins.txt`. Put `ui.pak` and the game files (`rac1.sprx` to `rac4.sprx` and `tod.sprx`) in `/dev_hdd0/qwark/`.

In standalone mode the client also keeps that copy current. If the console runs an older module than the client ships, the client sends the new one to the console and asks you to restart it. See [docs/standalone-update.md](docs/standalone-update.md).

## Game modules

Each game's support is a file of its own, named for the game: `rac1.sprx` (RaC1), `rac2.sprx` (RaC2), `rac3.sprx` (UYA), `rac4.sprx` (Deadlocked) and `tod.sprx` (Tools of Destruction). This keeps the module small at the home screen: your console only loads the file for the game that runs. The files sit beside `qwark.sprx` and `ui.pak`, and the client sends them wherever it sends `ui.pak`: the webMAN load, **Install to boot_plugins.txt**, the standalone update and the console installer. To copy them by hand, put them in `/dev_hdd0/qwark/` beside `ui.pak`.

Your console loads a game's file when that game starts and it has the memory for it. Until then the game has no game features: the status line names the game, the Memory panel works, and the Game panel says why.

- **Waiting for memory**: your console has too little free memory right now. The status line says "loading...", and its tooltip and the Game panel show how much memory your console has and how much it needs. The game support loads by itself when there is room. If it waits more than 30 seconds, your console shows a notification with the same figures.
- **Missing, damaged or from another build**: the status line shows it in red, and the client tells you once. Press **Send rac1.sprx** (or the file for your game) on the Connection panel, the Game panel or the status line. Your console loads it without a restart.

Under RPCS3 there are no game files: `qwark-rpcs3.exe` has every game in it.

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

Every category is in the game's planet order: each save goes under the planet it was made on, which the client reads from the save itself, whatever the save is called. A revisit has its own place after the planets before it, such as RaC1's second visit to Veldin, RaC2's return to Aranos and ToD's Fastoon II. Saves on the same planet are alphabetical, with numbers in number order ("Kerwan 9" before "Kerwan 10"). Saves whose planet the client cannot read come last, also alphabetical: a file that is not a save of this game, and a ToD save that is only on your console. The order of the planets is the line order of the autosplitter's `<game>-planets.txt` files, which is the game's own level order. In UYA that is not quite the story order (Daxx comes before Annihilation Nation, for example), and Deadlocked's Dreadzone Station, which you return to between planets, comes first. To list saves alphabetically only, open **Settings**, and under **Files** set **Sort save files** to **Alphabetical**. To replace one of your saves with the game's current state, right-click it and choose **Override...**. It asks first, because the old save is gone afterwards, and it works whenever **Save on console** does.

## Run warnings

When a run starts with a cheat or a mod on that the run should not have, your console shows a notification such as **Warning: 2 cheats enabled**. A run starts where the autosplitter starts its timer. A death or a reload does not count.

- **Cheats** are the cheats of the game's Cheats page, fast loads included, and a few more such as RaC1's goodies menu and RaC2's resets on death. UYA's **Quick-select pause**, ToD's **Remove cutscenes** and the manips do not count.
- **Mods**: a mod whose author says it is allowed in speedruns says **This mod is legal for speedruns.** under its description on the Mods panel. Mods whose author says they are not allowed are counted at every run start. Unknown mods (the author has not said) are mentioned once, until you load another one.

Under RPCS3 the warnings go to qwark-rpcs3's log.

## Console menu

On a console, all four games and ToD have a Trainer menu on the TV. Open it from **Trainer** in the pause menu, where Help was (in ToD, where **Inventory** was, in the pause menu's first list), or press L1, R1, L2, R2, L3 and R3 together in play. ToD's Trainer menu uses the pause menu's own list and its Game Options screen, scrolls a page at a time like the game's lists (Down on a page's last row shows the next page, Up on the first page's top row goes round to the last), and works under RPCS3 too once RaCMAN has written its RPCS3 patch and the game has been restarted. The Combos panel can change that combo. Untick **In-game menus** there to keep the game's own Help (in ToD, Inventory) in the pause menu; the combo still opens the Trainer menu.

The Combos panel also has a **Toggle fast loads** combo. It has no buttons until you capture some, and the console says whether fast loads are now on or off.

If you change or clear the console menu combo, hover its row to see the default buttons, and capture them again to put it back.

The Trainer menu has a **Mods** page, after **Unlocks**, with a row for each mod of the running game: the mods on your console, then the ones only a USB drive has (see [Mods on a USB drive](#mods-on-a-usb-drive)). Each row is **Off**, **On** (loaded) or **On boot** (loaded, and loaded again at every boot). Changing a row loads or unloads the mod at once, and the Mods panel shows the change.

## Per-user settings

Your console can keep separate settings for each user on it. Each user gets their own position slots, the mods and cheats that load automatically, manip and planet choices, the **In-game menus** setting, and the chargeboot and HUD colours set for the boot. The mod and savefile libraries stay shared.

Turn it on with **Per-user settings** at the bottom of the Combos panel, while you are connected. If the signed-in user has no settings of their own yet, the client asks whether to copy the current settings or start from the defaults. Users who sign in later start from the defaults. To change user, sign in as someone else on the PS3 home screen; the line under the switch says whose settings you are editing.

Combos are shared by everyone, and so is the **Combos enabled** switch. To give the signed-in user their own combo for one action, press **Override** on that row and press the buttons. That row then shows in orange, and its buttons read **Override**, which captures the override again, and **Clear Override**, which removes only the override, so the shared combo comes back; press **Clear** again to clear the shared combo. Overrides are kept while per-user settings are off.

RPCS3 has a single user, so this setting does not show there.

## Mods on a USB drive

Your console can load mods from a USB drive. Put each mod folder in `qwark/mods/<TITLEID>/` or in `mods/<TITLEID>/` on the drive, for example `mods/NPEA00423/il-ghost/`. Any USB port works.

The Mods panel lists these mods as **On USB000** (or the port they are on). Nothing is installed until you press **Load** or tick **Auto**: then your console copies the mod from the drive and keeps it.

When the drive has a newer version of a mod than your console, the Console column says **update ready**, and your console takes it the next time the mod loads. Hover over the cell to see the USB version, your console's version and the path on the drive. A mod that is loaded right now keeps its files until it loads again. Your console compares the `#- version` line in each `patch.txt`, so give each new release a higher number. An older copy, or one with the same version, never replaces your console's copy. If the same mod is on more than one drive or folder, your console uses the newest. A copy with your console's version but different contents is not flagged in the column: the mod just says **ready**, and its tooltip warns that your console uses its own copy.

**Load** also uses your console's copy when it is newer than the one in your mods folder, and does not upload yours over it. Under RPCS3 there are no USB mods: use the mods folder on your PC.

## Chargeboot colours

In Going Commando and Up Your Arsenal, **Game**, then **Cosmetics**, has three chargeboot colour pickers: **primary front**, **primary back** and **tint**. They work on a console and under RPCS3.

**Presets** above the pickers holds your saved colour sets. Type a name and press **Save** to keep the colours you changed, and pick a preset to put them back. Pick **(none)** to put the game's own chargeboot colours back.

The colours stay until the game restarts. Tick **on boot** beside **Save** to have the console set them again every time the game boots, also with no PC connected. While the box is ticked, the console also keeps every colour you change.

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

## Tools of Destruction

ToD (NPEA00452, the PSN EU version 1.00) has a first set of features: **Fast loads**, **Remove cutscenes**, **Infinite ammo**, **Infinite health**, **God Ratchet**, Die, bolts, raritanium and challenge mode, the unlock table, and buttons for skill points and gold bolts on the **Collectables** tab. Position slots, the planet list and the input display work as in the other games. The planet list, the Trainer's Planets page and the Level flags panel name ToD's planets as the ship's planet selector does: Kerwan (Metropolis), Cobalia, Kortog (Stratus City), Fastoon, Voron Asteroid Belt (the first space battle), Mukow (the Imperial Fight Festival), Nundac Asteroid Ring (Apogee Space Station), Ardolis (the Pirate Base), Rakar Star Cluster (the second space battle), Rykan V, Sargasso, Kreeli Comet (IRIS), Viceron (Zordoom Prison), Verdigris Black Hole (the third space battle), Jasindu (Kerchu City), Ublik Passage (Slag's Fleet), Reepor (the Cragmite Ruins), Igliak (Meridian City), and Fastoon II for the return to Fastoon. ToD has no manip categories yet, so its **Manips** page holds **Remove cutscenes** and the cutscene cache (below). Its Trainer menu takes the place of **Inventory** in the pause menu (see Console menu above). Its **Autosplitter** times a run as the community does. A new game (or a challenge mode, or a Metropolis save from before the end of Metropolis) is a load into Metropolis on a file that has not beaten Metropolis yet: the timer resets as that load begins, and starts 0.45 seconds into Metropolis's fade in from black, as Ratchet starts to show, not at the load. That works the same from the title or from Metropolis itself, with Fast loads on or off. A death or a checkpoint never resets or starts the timer and never splits, even on a new file. On a file that has beaten Metropolis, Metropolis is just another planet. It splits on each new planet (the planet route reads `tod-planets.txt`, which knows each planet by its planet name and its level name, so splits named either way work), and takes its last split, **Tachyon defeated**, when the screen has faded to black after the hit that defeats Tachyon, before the ending cutscene. **Mission completed** is there to tick if your route wants it. Game time stops during the long loading screens exactly where the old ToD autosplitter stopped it (on the way into Metropolis), a planet reloaded is never a second split, and a quit to the XMB pauses game time until the game is back at its title, then adds 19.95 s, as the old ToD autosplitter did. Under RPCS3, stopping the game is the quit, and pausing the emulator is not.

The **Save files** panel works in ToD as in the other games, on your console and under RPCS3, and ToD needs no RPCS3 patch for it. A save is the game's last save: its last autosave, a save from its menu, or the last save it loaded. Loads go through the game's own loading screen and work during gameplay only, whichever way you got there (Continue on the title screen, a load from the game's menu, or a new game that has never saved). When a load or a save is refused, the message says what your console said: the game isn't in gameplay yet, it's still on the title screen, a planet load is under way, the game is saving on its own, or the file isn't a Tools of Destruction save. **Set aside file** keeps the set-aside save on your console, so the game's autosaves do not replace it. As in RaC1, RaC2 and UYA, **Load set-aside** loads with fast loads on, even when **Fast loads** is off; a file loaded from the library loads at the game's own pace. RPCS3 keeps ToD's saves in `dev_hdd0/home/<user>/savedata/BCES00052_SAVE_<n>/`, and a `GAME.SAV` there that RPCS3 wrote itself is a ToD `.sav` file as it is: copy it into the library folder to load it from the panel. One copied over from a console is still encrypted and will not load.

- **Fast loads** ends a planet load as soon as the level's data is in. Without it the game plays its flight scenes to the end: the take-off, the "long load" scenes while the level loads, and the landing. With it the screen goes black once the level is loaded and the planet starts, with no landing scene. On your console it is a code patch, like the other games' Fast loads; under RPCS3 it needs qwark's RPCS3 patches, as theirs do. **Toggle fast loads** flips it. The first loads into Metropolis and Cobalia on a new file play a story video instead of the flight, and Fast loads does not cut those.
- **Remove cutscenes**, on **Game**, then **Manips**, skips the eight unskippable videos that runners delete from their game's files, without deleting anything: your console's game files stay as they are. It is legal for speedruns, so its **on boot** box starts ticked and it applies itself every time the game boots. Untick **on boot** to keep it off. Loads that RaCMAN starts into a new file's Metropolis or Cobalia, which play a story video instead of the flight, fade out over a few frames rather than one, so the screen does not flash when the video is skipped.
- **Cache cutscenes**, also on **Manips**, under **Cutscene cache**, makes Cobalia's, Stratus City's and Fastoon's opening cutscenes start sooner on your console, by up to about 2 seconds each. The first time you reach a planet in a game session its opening cutscene reads its sound from the game's files; after you have played the planet once, the game has that sound in its cache and the cutscene starts faster. Cache cutscenes puts the three planets' cutscene sound in the cache ahead of time. Press it once you are in gameplay, or before (it waits for gameplay), and keep playing: it runs in the background while you play, in the order a run needs them (Cobalia first, then Stratus City, then Fastoon), ahead of the game's own caching of the planet you are on, and skips whatever is cached already. The line under the button shows how far it has got, how fast your console read once it is done (the MiB/s), and says so if it stopped. Changing planet does not stop it: what is left goes on on the next planet. Reaching one of the three planets before its sound is cached is too late for that planet's cutscene, but the rest goes on, and the line says so once it has finished. Going back to the title stops it, but what it cached by then stays cached. The cache lasts until the game is closed, so a return to the title screen keeps it, and quitting to the XMB clears it. On RPCS3 cutscenes start almost at once anyway, so it makes little difference there.
- **Cache cutscenes on reset** does the same by itself whenever you start a new run: a new file (a new game, a challenge mode, or a save from before the end of Metropolis) loading into Metropolis. It starts with Metropolis's first moments of gameplay, ahead of the game's own caching of Metropolis, and normally has Cobalia done before you leave Metropolis. Then it carries on on Cobalia for Stratus City and Fastoon, so they are cached before you reach them at a normal run's pace. After the first run in a game session the sound is usually in the cache already, and then nothing more is read. It has an **on boot** box.
- **Disable cutscene caching** is for practice: while it is on, each planet's cutscene sound is cleared from the cache as the planet loads, so its opening cutscene always loads as slowly as the first time. Turn it off to go back to normal. While it is on, Cache cutscenes and its reset option do nothing.
- **Infinite ammo** also keeps your devices full, the Visi-Copter included.
- **Infinite health** is the game's own no-damage switch. It used to be called God Ratchet. It and **Infinite ammo** have an **on boot** box, as in the other games.
- **God Ratchet** is the old RaCMAN's god ratchet, the one speedrunners use: it also makes enemies ignore Ratchet. It sits beside the values table, where RaC1's goodies menu is.
- The Unlocks panel lists weapons, devices, gadgets and items. Use the Collectables buttons for skill points and gold bolts, and the RYNO's own row for the RYNO.

ToD's support is its game file, `tod.sprx`, which every release carries (see [Game modules](#game-modules)).

**Game**, then **Debug**, has a **Lua console**: a script editor that runs Lua in the game on your console. Type a script, then press **Run** or Ctrl+Enter. The whole script runs at once, in the game's own Lua (version 5.0.2), and its first return value, or its error, shows under the editor, with the game's last script error above it. For example `time_set_scale(0.5)` runs the game at half speed and `time_set_scale(1)` puts it back. The editor colours Lua's keywords, strings, numbers and comments, and you can drag its bottom edge to make it taller. A script can be at most 8192 bytes: the counter under the editor turns red, and **Run** waits, until it fits. To keep a script, type a name and press **Save**: it goes into the `lua` folder of your data folder, as a `.lua` file. **Open a saved script** opens one from there, **Open file...** opens any `.lua` file, and **Open folder** shows the folder. The editor asks before it opens a script over changes you have not saved. On a console, the first run after the game starts puts qwark's Lua helper into the game. Under RPCS3 the Lua helper is an RPCS3 patch: press **Install qwark patches...** on the Connection panel, then restart the game in RPCS3.

The **Level flags** panel lists ToD's level flags by name, grouped by planet, as the old RaCMAN did. Each box shows the flag as it is in your console's game, and a click sets or clears it. **Reset** under a planet clears that planet's flags and **Check all** beside it sets them all, both leaving the other planets' flags alone. The filter box finds a flag by name or by number.

## Updates

The client asks GitHub for a newer release once a day. If there is one, a bar appears at the top of the window. Press **Download**, then **Restart to update**. The client downloads nothing until you press the button.

**What's new** on the bar shows the release notes. The first start after an update offers them once more.

The Settings panel holds the switch, a **Check now** button and the version you run. The installer and the portable build can update themselves. A folder that you built yourself cannot.

A new client can carry a newer console module. The console keeps the module it already loaded, so the header says that `qwark.sprx` is out of date. The Connection panel then tells you how to send the new one. In standalone mode the client sends it by itself.

## Your files

Your settings, colour presets, watchlists, Lua scripts, save files and mods are in the data folder. An update never touches it.

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
