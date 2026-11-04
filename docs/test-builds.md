# Test builds

A test build of qwark is a build made between two releases, for trying out changes on a console. Since qwark build 57 a test build has no features of its own: game modules, per-user settings, USB mods, the Trainer menu's **Mods** page and Tools of Destruction all ship in releases, and the [user guide](../USER-GUIDE.md) describes them.

A test build differs from a release in one way: it says that it is a test build, and which one. The client compares that with the `qwark.build` beside its own `qwark.sprx`:

- When your console runs another test build than the one this client ships, or a release of the same number while this client ships a test build, the client treats it as out of date and replaces it, as it replaces an older build: by itself in standalone mode, with the out-of-date warning and the Load button in webMAN mode.
- When the two are the same test build, nothing is sent again.

The Connection panel names the test build your console runs and the one this client ships, so you can tell them apart.
