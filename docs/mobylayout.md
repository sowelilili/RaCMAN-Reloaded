# The moby layout files

A moby is one object in the game world. The console gives the client the address of the moby table
and the length of one row. It does not say what is in a row. The files in `data/moby/` do that.

There is one file per game: `Rac1.json`, `Rac2.json`, `Rac3.json` and `Rac4.json`. The client reads
them at the start, from the application folder. An update replaces them. A game with no file gets
the row index, the address and the position only.

The offsets in these files came from the structs of the old client. The file says so in its
`source` field. Keep that field correct when you change a file.

## The top of the file

| Key | What it is |
|---|---|
| `game` | Which game the file is for: 1, 2, 3 or 4. Two files cannot claim the same game. |
| `name` | The name the Mobys tab shows for the layout. |
| `source` | Where the offsets came from, so a later reader can check them again. |
| `stride` | How many bytes one row holds. The console's own stride is what the client reads with. |
| `fields` | The columns the Mobys table draws. |
| `struct` | The whole row, field by field. The inspector lists this. |

## The `fields` map

`fields` holds the four values the table has columns for. The key is the name the client asks for:
`position`, `state`, `oClass` and `uid`. Each entry has an `offset`, a `type` and a `from`.

```json
"fields": {
  "oClass": { "offset": 166, "type": "i16", "from": "short oClass" }
}
```

`offset` is the number of bytes from the start of the row. It is a decimal number. `from` names the
struct field the offset came from. Only these four keys mean something. The client ignores others.

## The `struct` list

`struct` is the row in the order the game stores it. Each entry has a `name`, an `offset` and a
`type`. A `bytes` entry also has a `length`.

```json
"struct": [
  { "name": "position", "offset": 16,  "type": "vec4f" },
  { "name": "state",    "offset": 32,  "type": "i8" },
  { "name": "rMtx",     "offset": 192, "type": "bytes", "length": 48 }
]
```

Double-click a row in the Mobys tab to open the inspector. The inspector shows one line for each
entry of this list: the name, the offset, the type and the live value.

Three rules apply to the list:

- The entries are in order of offset.
- Two entries do not overlap.
- The last entry ends at the stride. The list covers the whole row.

A field that nobody has identified keeps a placeholder name, for example `field19_0x36`. Do not
remove it. The name tells the reader that the bytes are not known, and the list must stay complete.

A field of the `fields` map must also be in the `struct` list, at the same offset.

## The types

| Type | Bytes | How the inspector shows it |
|---|---|---|
| `u8`, `u16`, `u32` | 1, 2, 4 | Decimal, without a sign. |
| `i8`, `i16`, `i32` | 1, 2, 4 | Decimal, with a sign. |
| `u64` | 8 | Hex. |
| `f32` | 4 | A number with up to four decimals. |
| `ptr` | 4 | Hex, because the value is an address. |
| `vec3f` | 12 | Three floats, separated by commas. |
| `vec4f` | 16 | Four floats, separated by commas. |
| `bytes` | `length` | Hex. |

All values are big-endian, because that is how the console stores them.

A box takes back what it shows. Press Enter to write the value to the console. A `bytes` field is
shown and never written: the client does not know what is in one.

Right-click a field for the menu. A field of 1, 2 or 4 bytes can become a watch. A vector is offered
as its floats, one watch for each. A field of 8 bytes and a `bytes` field cannot become a watch,
because a watch is 1, 2 or 4 bytes. A `ptr` field can also send its target to the Viewer tab.

A type the client does not know has no length. The inspector shows a dash for it.

## To extend a file

1. Find the field in the struct the `source` field names.
2. Add an entry to the `struct` list, in order, with the offset and the type.
3. Keep the list complete: if you split a `bytes` block, the parts must cover the same bytes.
4. Start the client again. The files are read once, at the start.
