# Import from RaCMAN

The old RaCMAN kept everything in one folder, beside `racman.exe`: `config.txt`, a `savefiles` folder and a `mods` folder. This client keeps the same three things in its own data folder. **Import from RaCMAN**, on the Settings panel, brings all of them over in one press.

## What to point it at

Point the box at the old `config.txt` and press **Import**. The folder that file sits in is the old RaCMAN folder, so the save files and the mods beside it come over with the settings. The section lists what it found before you press, for example "In that folder: 12 saves in 3 categories, 4 mods".

The save files and the mods are files on this PC, so they are imported whether or not a console is connected. Only the button combos and the auto-apply mod flags need a connection: those live on the console now. If you were not connected, import again once you are.

## Nothing you have is replaced

The old folder is never changed and nothing already in your data folder is overwritten.

**Save files.** Every file under `savefiles/<TITLEID>/<category>/` is copied into the same category here. The old client let a save have any name at all, and this one needs `.sav`, so a name without a suffix gets one. A save whose bytes are already in that category is not copied again, whatever either copy is called. If the name is taken by a different save, the new one arrives as `<name> (2).sav`.

**Mods.** A mod is its whole folder, so whole folders are compared. One that ships with this client already, byte for byte, is not copied. Neither is one you already have, whatever folder name you have it under. If you have a folder of that name holding something else, the old mod arrives beside it as `<modfolder> (imported)`, because the folder name is the name the console loads a mod by and only one mod can have it. Both are then in your mod list and you can see which you want.

After the import the client says what it did: how many saves and mods came over, how many were renamed, and how many were already here.

Nothing outside `savefiles` and `mods` is touched. If the old folder has neither, the import is only the settings.

## Save files copied by hand

You do not have to use the import. If you copy an old `savefiles` folder into the data folder yourself, the client puts the suffix on the files the first time it opens that game's save file list, and says how many it renamed. A file that already has a suffix of its own — a `.txt`, a `.bak` — is left alone, because it is not a save.
