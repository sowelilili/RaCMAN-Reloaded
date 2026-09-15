# Import from RaCMAN

The old RaCMAN kept everything in one folder, beside `racman.exe`: `config.txt`, a `savefiles` folder and a `mods` folder. This client keeps the same three things in its own data folder. **Import from RaCMAN**, on the Settings panel, brings all of them over in one press.

## What to point it at

Point the box at the old `config.txt` and press **Import**. The folder that file sits in is the old RaCMAN folder, so the save files and the mods beside it come over with the settings. The section lists what it found before you press, for example "In that folder: 12 saves in 3 categories, 4 mods".

The save files and the mods are files on this PC, so they are imported whether or not a console is connected. Only the button combos and the auto-apply mod flags need a connection: those live on the console now. If you were not connected, import again once you are.

## Nothing you have is replaced

The old folder is never changed and nothing already in your data folder is overwritten.

**Save files.** Every file under `savefiles/<TITLEID>/<category>/` is copied into the same category here. The old client let a save have any name at all, and this one needs `.sav`, so a name without a suffix gets one. A save whose bytes are already in that category is not copied again, whatever either copy is called. If the name is taken by a different save, the new one arrives as `<name> (2).sav`.

**Mods.** A mod is its whole folder, so whole folders are compared. One that ships with this client already, byte for byte, is not copied. Neither is one you already have, whatever folder name you have it under. If you have a folder of that name holding something else, the old mod arrives beside it as `<modfolder> (imported)`, because the folder name is the name the console loads a mod by and only one mod can have it. Both are then in your mod list and you can see which you want.

After the import the client says what it did: how many saves and mods came over, how many were renamed, how many were already here, and which mods were left behind and why.

Nothing outside `savefiles` and `mods` is touched. If the old folder has neither, the import is only the settings.

## Mods that are not imported

Not every mod in an old folder is one this client can use, so the import leaves some of them where they are. It never deletes anything: the old folder keeps its copy, and the section on the Settings panel counts them apart from the rest, for example "In that folder: 12 saves in 3 categories, 4 mods, 6 excluded".

These are the rules, in the order they are applied to each folder under `mods/<TITLEID>/`:

1. **`mods/libs/` is skipped.** It is the Lua the old client's scripts shared, not a game's mods.
2. **A folder with no `patch.txt` is not a mod.** The old library keeps the source a few mods were built from next to the mods themselves. Nothing is said about these; there is nothing there to import.
3. **A mod on the exclusion list is left behind**, with the reason the list gives. These are the old RaCMAN's own default mods that this client decided not to ship: the savefile and autosplitter helpers qwark carries itself now, the ones retired for fighting the savefile helper over the same hook, the two trainers that are one mod here, and the one this client never shipped.
4. **A savefile helper is left behind**, wherever it sits and whatever it is called: qwark does savefiles itself now, and a second one on the same hook breaks both. A mod counts as one when its `patch.txt` calls it a savefile helper or a savefile manager, or when its folder is named `sfhelper`, ends in `-save`, or has `sfhelper` or `savefile` in it. That catches the copies from every old release, renamed ones included.
5. **A mod that needs Lua is left behind.** The old client ran Lua scripts and this one does not, so the mod would be a checkbox that does nothing. A mod needs Lua when its `patch.txt` has an `automation:` line or when there is a `.lua` file anywhere in its folder.

Everything else is imported under the rules above: nothing you have is replaced.

The list is a plain text file, `data/legacy-mod-exclusions.txt` beside the client's own program, one entry per line as `TITLEID/modfolder`, then a space, then the short reason the import prints. Lines starting with `#` are comments. Add a line to keep another mod out of an import, or take one out to let that mod come over — but note that the folder is the client's own and an update replaces it, so an edit there is worth making again after one. The last two rules hold whatever the file says, so a savefile helper or a Lua mod is left behind even with the file deleted.

## Save files copied by hand

You do not have to use the import. If you copy an old `savefiles` folder into the data folder yourself, the client puts the suffix on the files the first time it opens that game's save file list, and says how many it renamed. A file that already has a suffix of its own — a `.txt`, a `.bak` — is left alone, because it is not a save.
