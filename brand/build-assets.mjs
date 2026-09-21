// Renders the brand SVGs into the files the three front ends need.
//
//   cd brand && npm install --no-save sharp && node build-assets.mjs
//
// sharp is not a dependency of anything in this repository: it is installed
// for this script only, when the logo changes, and the outputs are committed.
// A build that needed a native image library to produce an icon would be a
// build that breaks on the next machine for a reason nobody remembers.

import sharp from 'sharp';
import { mkdir, writeFile, copyFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..');
const mark = join(here, 'blinkylite-mark.svg');
const favicon = join(here, 'blinkylite-favicon.svg');
const logo = join(here, 'blinkylite-logo.svg');

// Up to 48 px the simplified favicon, above it the full mark: the arc and the
// glow are what makes the big one, and what smudges the small one.
const source = (size) => (size <= 48 ? favicon : mark);

async function png(svg, size) {
  const viewBox = svg === favicon ? 64 : 512;
  return sharp(svg, { density: Math.max(72, (72 * size) / viewBox) })
    .resize(size, size)
    .png()
    .toBuffer();
}

// An .ico with PNG entries: supported by Windows since Vista, by WPF and by
// every browser, and it keeps the 256 px entry small.
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);

  let offset = header.length;
  images.forEach(({ size, data }, i) => {
    const entry = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2);
    header.writeUInt8(0, entry + 3);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(data.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += data.length;
  });

  return Buffer.concat([header, ...images.map((i) => i.data)]);
}

async function icoOf(sizes) {
  return ico(await Promise.all(sizes.map(async (size) => ({ size, data: await png(source(size), size) }))));
}

async function write(path, data) {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, data);
  console.log('wrote', path.replace(repo, '.'), `${data.length} B`);
}

// GitHub: an <img> of an SVG cannot load the mark the logo refers to, so the
// README shows the rendered PNG.
await write(join(here, 'blinkylite-logo.png'), await sharp(logo, { density: 144 }).resize(1280, 400).png().toBuffer());
await write(join(here, 'blinkylite-mark-512.png'), await png(mark, 512));

// Angular: the SVG favicon for browsers that take it, the .ico for the rest,
// the mark for the sidebar and the sign-in card, a 512 px icon for pinning.
await copyFile(favicon, join(repo, 'web/public/favicon.svg'));
await copyFile(mark, join(repo, 'web/public/brand/blinkylite-mark.svg')).catch(async () => {
  await mkdir(join(repo, 'web/public/brand'), { recursive: true });
  await copyFile(mark, join(repo, 'web/public/brand/blinkylite-mark.svg'));
});
await write(join(repo, 'web/public/favicon.ico'), await icoOf([16, 32, 48]));
await write(join(repo, 'web/public/brand/icon-512.png'), await png(mark, 512));
await write(join(repo, 'web/public/brand/apple-touch-icon.png'), await png(mark, 180));

// WPF: the application icon (Explorer, taskbar, Alt+Tab) and the window icon.
await write(join(repo, 'src/BlinkyLite.Client/Assets/blinkylite.ico'), await icoOf([16, 20, 24, 32, 40, 48, 64, 128, 256]));
await write(join(repo, 'src/BlinkyLite.Client/Assets/blinkylite-mark-128.png'), await png(mark, 128));

// MSIX (0052): the tiles and the list icon Windows asks the package for, and
// the .ico the desktop shortcut points at.
const msix = join(repo, 'packaging/client/Assets');
await write(join(msix, 'Square44x44Logo.png'), await png(source(44), 44));
await write(join(msix, 'Square44x44Logo.targetsize-24_altform-unplated.png'), await png(source(24), 24));
await write(join(msix, 'Square150x150Logo.png'), await png(mark, 150));
await write(join(msix, 'StoreLogo.png'), await png(mark, 50));
await write(join(msix, 'blinkylite.ico'), await icoOf([16, 20, 24, 32, 40, 48, 64, 128, 256]));
