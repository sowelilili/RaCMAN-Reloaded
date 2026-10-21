# RaCMAN Reloaded

A modding tool and trainer for the original Ratchet & Clank quadrilogy, on PS3 and RPCS3, tailored for speedrunners. It's
- Standalone: Powered by the qwark server, which runs locally on your console for better performance, with an optional PC client.
- Seamless: connection, reconnection and switching games is seamless and happens automatically.
- Portable: supports Windows and Linux (macOS coming soon?), PS3 and RPCS3.
- Powerful: all of RaCMAN's features, a savefile manager for every game, and more.
- Customizable: and it even has a dark mode :)

# Installation

The qwark server can run standalone on your PS3 to provide the core trainer functionality. Save file tools and run manipulations are also included, so it's everything you need to start running the game optimally. Optionally, you can also install the PC client for the full feature set: full mod and save file management, LiveSplit and OBS integration, and cosmetic options. 

**To install qwark on your console**, download [qwark-installer.pkg](https://github.com/sowelilili/RaCMAN-Reloaded/releases/latest/download/qwark-installer.pkg) and run it on your modded PS3. This unlocks the in-game trainer menu, which has everything you need to start running any of the four games optimally.

**To install RaCMAN Reloaded on your PC**, download the standalone installer ([windows](RaCMANReloaded-win-Setup.exe), [linux](RaCMANReloaded.AppImage)) and run it on your device. If you have [WebMAN MOD](https://github.com/aldostools/webman-mod/) installed, RaCMAN Reloaded can install qwark for you. Otherwise, download [qwark-installer.pkg](https://github.com/sowelilili/RaCMAN-Reloaded/releases/latest/download/qwark-installer.pkg) and run it on your PS3. The default connection mode is via WebMAN MOD, so you'll need to change it to "standalone" in the settings.

# Usage

Currently, the project supports the original Ratchet & Clank quadrilogy. Currently, only the PAL digital download versions (NPEA00385, NPEA00386, NPEA00387, NPEA00423) and the PAL trilogy disc version (BCES01503) are supported. The fan-made projects Black Label Mod (BLMODYOOO) and RaC2 IL Mod (RC2ILYOOO) are also supported.

To use the standalone trainer, just install qwark and boot one a supported game. To connect RaCMAN Reloaded from your PC, ensure your PS3 and PC are on the same network. Enter your PS3's IP and press connect. Windows users may see a firewall warning; in most cases the program should be able to silence it with the included helper. RaCMAN Reloaded uses the PINE protocol for RPCS3 support. To connect to RPCS3, ensure the PINE server is running (Manage > Network Services > IPC). To connect to LiveSplit, first start the LiveSplit TCP Server (Control > Start TCP Server) or enable it on-boot (Settings > LiveSplit Server > Startup Behavior). As of 2026-10-21, **the [LiveSplit Development Build](https://livesplit.org/downloads/)** is required.

More information can be found in the [user guide](https://github.com/sowelilili/RaCMAN-Reloaded/blob/main/USER-GUIDE.md). For info on building and developing, see the [developer guide](https://github.com/sowelilili/RaCMAN-Reloaded/blob/main/BUILD.md).
