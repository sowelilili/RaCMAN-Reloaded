# The Game page layout

The console tells the client which features a game has. The client decides where to draw them.
`data/gamelayout.json` holds those decisions. You can change the file. You do not have to rebuild
anything.

## Which copy is read

The release puts a copy in the application folder. An update replaces that copy.

To keep your changes, put a file named `gamelayout.json` in the data folder. The client reads that
file instead. **Game layout**, on the **Advanced** tab of the Settings page, shows which of the two
files is in use, and its **Reload gamelayout.json** button reads the file again while the client runs.

## The two lists at the top of the file

| List | What it does |
|---|---|
| `subPages` | The section becomes a page of its own. The side nav shows it under Game. |
| `unlocksTabs` | The Unlocks panel draws the section as a tab, beside the unlock categories. |

The file that ships lists Manips, Cosmetics and Debug as sub-pages, and Collectables as a tab.

A section in neither list stays on the Game page. A section in both lists stays a sub-page, because
the client never draws a section twice. `sideSections` is the old name for `subPages`, and still
works.

Some games have no unlock table. Those games have no Unlocks entry in the side nav. For those games
the client draws the `unlocksTabs` sections under Game, so nothing is lost.

## Per-game entries

The `games` entries are keyed by game: `rac1`, `rac2`, `rac3`, `rac4` and `tod`. The disc release
BCES01503 holds three games under one title id, so a game key is what gives each of them the correct
layout. An entry keyed by a title id is also possible. The client reads it first.

Each entry can hold these tables.

**`moves`** sends one feature to a named section. The key is the feature label, exactly as the
Connection panel and the Game page show it. The client makes the section if it does not exist.

**`tabOrder`** sets the order of the sections.

**`headings`** divides one section into groups. The first key is the section. The second key is the
heading text. The value is the list of features below that heading.

```json
"headings": {
  "Cosmetics": {
    "Chargeboots": ["Chargeboots primary front", "Chargeboots primary back", "Chargeboots tint"]
  }
}
```

UYA's Cosmetics sub-page then shows the armour and the ship colour first. A **Chargeboots** heading
follows, with those three rows below it. Features that no heading names keep their position. A
section that `headings` does not mention does not change.

## The four reserved sections

Each of these four names belongs to a part of the screen that a panel controls. A `moves` entry can
send a feature to one of them. They cannot be sub-pages, tabs or `tabOrder` entries.

| Name | Where the feature goes |
|---|---|
| `Quick` | The block of buttons at the top of the Game page |
| `Values` | The editable table below that block |
| `Options` | The column beside that table |
| `Unlocks` | The Unlocks panel, above its table |

Features of type VALUE go to `Values` if no rule moves them. This is how the QE values reach the
Debug page.

`Player` is an ordinary section. The Game page also takes three features wherever they are: the two
save file actions, and an action the game calls "Die". A section with nothing else left in it draws
no heading.

`Manips` is an ordinary section too. On a game with manip categories it starts with the category
controls, and the client shows it even when no feature is in it. The console can also list features
under headings of its own there: Deadlocked's **Fixes** and UYA's **Trophies**. Those features are
always drawn under their heading, so a `moves` entry for one of them has no effect.

Since qwark build 59 (revision 1.34) the console says which features are run-legal, and the client draws every one of them on `Manips`: a `moves` entry for one has no effect, and the layout never decides what is run-legal. The collectable buttons are in a **Collectables** group of the console's own. A console still on an older build says neither, and for that console alone the client reads a game's **`olderModules`** table, label to section as `moves` is, which the shipped file uses for ToD's run-legal rows and the collectable buttons that older builds left in Progress. It is never read for a build 59 console.

`Debug` is an ordinary section as well. In a game with Lua (ToD) it starts with the Lua box, and
the client shows it even when no feature is in it.
