# The input display in OBS

The client can serve the input display as a page. OBS shows that page as a Browser Source. The page draws the pad with the same controller skins as the client, and it follows the console at the speed of the telemetry.

## Switch it on

The switch is in the **OBS** part of the Input display panel. It is on by default. The panel shows the URL, a **Copy URL** button, the size of the selected skin, and what the listener is doing.

The client serves the page on 127.0.0.1 only. No other machine can reach it. Windows does not filter loopback traffic, so this needs no firewall rule.

## Add the browser source

1. In OBS, add a **Browser Source**.
2. Put the URL in the **URL** box: `http://127.0.0.1:9674/pad`
3. Set **Width** and **Height** to the size the panel shows. For DS3 Black that is 800 by 558.
4. Set **FPS** to 60.
5. Switch on **Refresh browser when scene becomes active**.

The page has a transparent background, so leave the custom CSS as it is. Scale the source in OBS to make the pad larger or smaller.

## Two sources, two skins

Add `?skin=<name>` to the URL to give one source a skin of its own:

```
http://127.0.0.1:9674/pad?skin=Compact
```

The name is the folder name in `controllerskins/`. If the client does not have that folder, the source shows the skin the client has selected.

## When there is no data

The page makes the pad grey when the client is not connected, when no game runs, and when telemetry stops. It connects again by itself when the client starts again, so a source can stay in the scene. If you select another skin in the client, the page loads the new skin without a refresh.

## The port

The port is 9674 by default, one above the port qwark listens on. Change it in the same part of the panel. If another program holds the port, the panel says so and the rest of the client continues to work; the client tries again when you change the port.

The settings file holds the two keys, `obsPadEnabled` and `obsPadPort`.

## What the client serves

| Address | What it is |
|---|---|
| `/pad` | the page, which is one file inside the client |
| `/skin.json` | the skin's sprite rectangles, base size, analog pitch and sheet size |
| `/skin.png` | the skin's sprite sheet |
| `/events` | the pad mask and the four analog axes, one event per telemetry packet |

Each address takes the same `?skin=<name>`. Eight browser sources can watch at the same time.
