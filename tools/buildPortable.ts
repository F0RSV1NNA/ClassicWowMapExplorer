// Builds the portable version: release/MapExplorer/ (MapExplorer.exe next to the built app)
// and release/MapExplorer-portable.zip. The exe serves the app on 127.0.0.1 and opens it in
// the browser, so nothing needs installing to run it.
// Usage: bun run portable (needs gcc and windres from mingw-w64, ImageMagick and 7-Zip on PATH).
import { execFileSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const OUT = 'release/MapExplorer';
const ZIP = 'release/MapExplorer-portable.zip';
const WORK = '.cache/portable';
/** Sizes in the exe's icon, for everything from the title bar to large Explorer views. */
const ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 256];

const run = (command: string, args: string[]) => {
	console.log(`> ${command} ${args.join(' ')}`);
	execFileSync(command, args, { stdio: 'inherit' });
};

// Keep the existing portable browser profile and saved UI/editor settings.
mkdirSync(OUT, { recursive: true });
mkdirSync(WORK, { recursive: true });

// The app itself: type-checked and built to dist, then copied in as "app".
// Run with the same bun that is running this script.
console.log('> bun run build');
execFileSync(process.execPath, ['run', 'build'], { stdio: 'inherit' });
cpSync('dist', join(OUT, 'app'), { recursive: true });
// Explorer's own self-contained x64 fallback; no installed .NET/Mappster needed.
execFileSync(process.execPath, ['tools/buildNativeMeshEngine.ts'], { stdio: 'inherit' });

// The icon: each size rendered from the SVG on its own, so small ones stay sharp.
const pngs = ICON_SIZES.map((size) => {
	const png = join(WORK, `icon-${size}.png`);
	run('magick', ['-background', 'none', '-density', String(Math.max(96, size * 2)), 'public/icon.svg', '-resize', `${size}x${size}`, png]);
	return png;
});
run('magick', [...pngs, join(WORK, 'icon.ico')]);

// The launcher, with the icon and version details built in: a windowed program (no console),
// static, so it needs no DLLs.
const resource = join(WORK, 'launcher.res');
run('windres', ['--include-dir', WORK, 'tools/portable/launcher.rc', '-O', 'coff', '-o', resource]);
run('gcc', ['-O2', '-s', '-static', '-municode', '-mwindows', '-Wall', 'tools/portable/launcher.c', resource, '-lws2_32', '-lshell32', '-ladvapi32', '-luser32', '-o', join(OUT, 'MapExplorer.exe')]);

writeFileSync(join(OUT, 'README.txt'), `Map Explorer (portable)

Double-click MapExplorer.exe. Map Explorer opens in a window of its own, finds World of
Warcraft by itself (or asks for its folder if it can't), and you fly. Close the window when
you're done.

Keep MapExplorer.exe next to the "app" and "mesh-engine" folders. The window is Microsoft Edge (or Chrome) showing
the app, served from the "app" folder to this computer only; nothing is sent anywhere. Its
settings are kept in the "data" folder, apart from your own browser.
`.replace(/\n/g, '\r\n'));

if (existsSync(ZIP)) rmSync(ZIP);
run('7z', ['a', '-tzip', '-mx=9', '-xr!data', ZIP, `./${OUT}`]);
console.log(`\nPortable version: ${OUT}/ and ${ZIP}`);
