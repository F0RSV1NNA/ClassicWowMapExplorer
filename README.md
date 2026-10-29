# MapExplorer

Fly over the World of Warcraft world, drawn from your own game install. Everything is read straight from the game files on your computer; nothing is hosted, uploaded or downloaded.

Built for WoW Classic, with Eastern Kingdoms and Kalimdor loaded as one seamless world.

## Windows

You need **Windows 10 or 11** and **World of Warcraft Classic** installed.

1. Download **MapExplorer-portable.zip** from the
   [latest release](https://github.com/Coldensjo/ClassicWowMapExplorer/releases/latest) and unzip it anywhere.
2. Run **MapExplorer.exe**. If Windows says *"Windows protected your PC"* (the program isn't signed),
   click **More info**, then **Run anyway**.
3. It finds World of Warcraft by itself. If not, click **Choose your World of Warcraft folder** and pick
   the folder that contains `_classic_` or `_classic_beta_`. The files never leave your computer.

Fly with **W A S D**, hold **Shift** to go faster. Press **?** for all controls.

## Linux and macOS

Run from source. You need World of Warcraft Classic, Chrome or another Chromium-based browser, **git**
and **[Bun](https://bun.sh)**.

```sh
git clone https://github.com/Coldensjo/ClassicWowMapExplorer.git
cd ClassicWowMapExplorer
bun install
bun run dev
```

Then open **http://localhost:5173**. It looks for the game in the usual places (Wine, Lutris, Bottles,
Steam, Heroic on Linux; `/Applications` on macOS). If it can't find it, set `WOW_DIR` to the folder that
contains `_classic_` or `_classic_beta_`, or choose the folder in the page:

```sh
WOW_DIR="/path/to/World of Warcraft" bun run dev
```

macOS is untested.

## Troubleshooting

- **"That isn't the World of Warcraft folder"**: pick the folder one level up, the one that
  contains `_classic_` or `_classic_beta_`.
- **Nothing shows up**: use Chrome or Edge with hardware acceleration on and an up-to-date graphics driver.
- **Freezes or stutter**: press **F9** right after one to save a debug log and attach it to the issue.

## For developers

The **Mesh Generate**, **Mesh Editor** and **Mesh Options** menus provide collision
inspection, saved reachability flags with pink unreachable polygons, and Mappster's original
C# extraction/baking engine compiled to WebAssembly. It runs inside Explorer's
browser workers; no Mappster process or installed .NET runtime is required to run it.
Building from source requires the .NET 10 SDK and `dotnet workload install wasm-tools`.
See [mesh authoring](docs/mesh-authoring.md) for controls, output format and remaining work.
The ordered [Mappster parity plan](docs/mappster-parity-plan.md) tracks the bake-quality port.

- `bun run dev`: start the dev server
- `bun run build`: type-check and build to `dist`
- `bun run mesh:build`: compile and bundle the browser mesh engine
- `bun tools/checkMeshParity.ts`: focused native-versus-WASM output comparison
- `node tools/checkMeshBrowser.mjs`: check the built worker in headless Edge (after parity check)
- `bun run spawns`: rebuild the spawn files from a VMaNGOS SQLite database (`-- path/to/mangos.sqlite`)
- `bun run regions`: rebuild regions, flight paths, boats and zeppelins
- `bun run portable`: build the Windows portable version into `release/`

Source layout: `src/casc` (storage), `src/formats` (file formats), `src/worker` (parsing),
`src/explorer` (world data), `src/viewer` (rendering, controls, audio), `src/mesh`
(collision extraction, baking and projects), `tools` (Bun scripts).
