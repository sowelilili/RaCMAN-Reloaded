# The standalone module update

In standalone mode the console loads `qwark.sprx` itself at startup, from the path listed in `/dev_hdd0/boot_plugins.txt`. The boot install writes that path as the first line of the list, so HEN loads qwark before webMAN; a console that loads webMAN first has been seen to hang the XMB. The client never uses webMAN in this mode. So when a new client ships a newer module, the client replaces the file on the console through the module that is already running.

## When it runs

The client does this when all of these are true:

- the connection mode is Standalone;
- the console reports a build older than the one the client shipped with;
- a `qwark.sprx` is beside the client;
- the session is XMB or INGAME, and no save transfer is in progress.

It runs at most once per connection. It never installs an older module.

## What it does

1. Uploads the `ui.pak` beside the client as `/dev_hdd0/qwark/ui.pak.new`, reads it back and compares its length and CRC32 with the file it sent. If there is no `ui.pak` beside the client, it skips this step and says that the pak was not sent.
2. Reads `/dev_hdd0/boot_plugins.txt` and takes the line that names `qwark.sprx` as the boot path. If there is no such line, it uses `/dev_hdd0/plugins/qwark.sprx`.
3. Uploads the new module beside it as `qwark.sprx.new`.
4. Reads the upload back and compares its length and CRC32 with the file it sent.
5. Renames the old file to `qwark.sprx.old`, replacing any older `.old`.
6. Renames `qwark.sprx.new` to the boot path. If this step fails, the old file is put back.

The boot copy is changed only in the last two steps. Any failure before them leaves the console booting what it booted before. A failure shows one message that names the step.

The client never writes `ui.pak` itself here. The running module keeps its own `ui.pak` until the restart, because the new build's pak does not fit it. The new module moves `ui.pak.new` into place when it starts.

## Afterwards

The console loads a boot plugin only at startup. The header and the Connection panel ask you to restart the console, and keep asking until the console reports the new build. With "Show debug information" on, the Connection panel has a button that runs the same update by hand.
