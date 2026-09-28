// Erzeugt Favicon, PWA- und Apple-Icons in public/ aus den SVG-Quellen in
// diesem Ordner. Gerendert wird mit einem lokal installierten Chrome/Edge im
// Headless-Modus über das DevTools-Protokoll (keine zusätzliche
// npm-Abhängigkeit, Node >= 22 für das eingebaute WebSocket). Die Ergebnisse
// werden eingecheckt; das Skript läuft nur, wenn sich das Motiv ändert:
//
//   node icon-source/generate-icons.mjs          (aus stempeluhr-client/)
//
// Browser-Pfad bei Bedarf über CHROME=/pfad/zu/chrome vorgeben.
//
// Nicht über `chrome --screenshot --window-size=...`: headless hält eine
// Mindestbreite des Fensters ein und zentriert die SVG darin, kleine Größen
// kommen dann als Ausschnitt heraus.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const sourceDir = dirname(fileURLToPath(import.meta.url));
const publicDir = resolve(sourceDir, '../public');
const tile = join(sourceDir, 'stempeluhr.svg');
const maskable = join(sourceDir, 'stempeluhr-maskable.svg');
const transparent = { r: 0, g: 0, b: 0, a: 0 };
const primary = { r: 0x21, g: 0x57, b: 0xd6, a: 1 };

const browserPath = [
  process.env.CHROME,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
].find((path) => path && existsSync(path));
if (!browserPath) {
  throw new Error('Kein Chrome/Edge gefunden - Pfad über CHROME=... angeben.');
}

// Minimaler DevTools-Client: Befehle per id, Ereignisse an Listener.
async function launchBrowser(profileDir) {
  const proc = spawn(
    browserPath,
    [
      '--headless',
      '--disable-gpu',
      '--no-first-run',
      '--no-default-browser-check',
      '--remote-debugging-port=0',
      `--user-data-dir=${profileDir}`,
      'about:blank',
    ],
    { stdio: 'ignore' },
  );
  const portFile = join(profileDir, 'DevToolsActivePort');
  let endpoint;
  for (let attempt = 0; attempt < 100 && !endpoint; attempt++) {
    const [port, path] = existsSync(portFile) ? readFileSync(portFile, 'utf8').split('\n') : [];
    if (port && path) {
      endpoint = `ws://127.0.0.1:${port}${path}`;
    } else {
      await sleep(100);
    }
  }
  if (!endpoint) {
    proc.kill();
    throw new Error(`${browserPath} hat keinen DevTools-Port geöffnet.`);
  }

  const socket = new WebSocket(endpoint);
  await once(socket, 'open');
  let nextId = 1;
  const pending = new Map();
  const listeners = new Set();
  socket.addEventListener('message', ({ data }) => {
    const message = JSON.parse(data);
    const request = pending.get(message.id);
    if (request) {
      pending.delete(message.id);
      if (message.error) {
        request.reject(new Error(`${request.method}: ${message.error.message}`));
      } else {
        request.resolve(message.result);
      }
    } else {
      listeners.forEach((listener) => listener(message));
    }
  });

  const send = (method, params = {}, sessionId) =>
    new Promise((resolvePromise, reject) => {
      const id = nextId++;
      pending.set(id, { method, resolve: resolvePromise, reject });
      socket.send(JSON.stringify({ id, method, params, sessionId }));
    });
  const waitFor = (predicate) =>
    new Promise((resolvePromise) => {
      const listener = (message) => {
        if (predicate(message)) {
          listeners.delete(listener);
          resolvePromise(message);
        }
      };
      listeners.add(listener);
    });
  const close = async () => {
    await send('Browser.close').catch(() => {});
    if (proc.exitCode === null) {
      await once(proc, 'exit');
    }
  };
  return { send, waitFor, close };
}

// Rendert die SVG formatfüllend in size x size Pixel. Ein transparenter
// Hintergrund lässt die runden Ecken der Kachel frei.
async function render(browser, sessionId, svg, size, background = transparent) {
  const svgUrl = `data:image/svg+xml;base64,${readFileSync(svg).toString('base64')}`;
  const html =
    '<!doctype html><html><body style="margin:0">' +
    `<img src="${svgUrl}" width="${size}" height="${size}" style="display:block">` +
    '</body></html>';
  await browser.send(
    'Emulation.setDeviceMetricsOverride',
    { width: size, height: size, deviceScaleFactor: 1, mobile: false },
    sessionId,
  );
  await browser.send('Emulation.setDefaultBackgroundColorOverride', { color: background }, sessionId);
  const loaded = browser.waitFor(
    (message) => message.method === 'Page.loadEventFired' && message.sessionId === sessionId,
  );
  await browser.send(
    'Page.navigate',
    { url: `data:text/html;base64,${Buffer.from(html).toString('base64')}` },
    sessionId,
  );
  await loaded;
  const { data } = await browser.send(
    'Page.captureScreenshot',
    { format: 'png', clip: { x: 0, y: 0, width: size, height: size, scale: 1 } },
    sessionId,
  );
  const png = Buffer.from(data, 'base64');
  if (png.readUInt32BE(16) !== size || png.readUInt32BE(20) !== size) {
    throw new Error(`${svg}: ${size}px erwartet, Screenshot hat eine andere Größe.`);
  }
  return png;
}

// ICO-Container mit eingebetteten PNGs (von allen aktuellen Browsern und
// Windows unterstützt).
function ico(pngs) {
  const header = Buffer.alloc(6 + 16 * pngs.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(pngs.length, 4);
  let offset = header.length;
  pngs.forEach(({ size, png }, i) => {
    const entry = 6 + 16 * i;
    header.writeUInt8(size % 256, entry);
    header.writeUInt8(size % 256, entry + 1);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...pngs.map(({ png }) => png)]);
}

const workDir = mkdtempSync(join(tmpdir(), 'stempeluhr-icons-'));
try {
  const browser = await launchBrowser(join(workDir, 'profile'));
  try {
    const { targetId } = await browser.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await browser.send('Target.attachToTarget', { targetId, flatten: true });
    await browser.send('Page.enable', {}, sessionId);
    const png = (svg, size, background) => render(browser, sessionId, svg, size, background);

    mkdirSync(join(publicDir, 'icons'), { recursive: true });
    copyFileSync(tile, join(publicDir, 'favicon.svg'));
    const icoImages = [];
    for (const size of [16, 32, 48]) {
      icoImages.push({ size, png: await png(tile, size) });
    }
    writeFileSync(join(publicDir, 'favicon.ico'), ico(icoImages));
    writeFileSync(join(publicDir, 'icons/icon-192.png'), await png(tile, 192));
    writeFileSync(join(publicDir, 'icons/icon-512.png'), await png(tile, 512));
    writeFileSync(join(publicDir, 'icons/icon-maskable-512.png'), await png(maskable, 512));
    // iOS rundet die Ecken selbst und verlangt ein Bild ohne Transparenz: die
    // Kachel auf deckendem Primärblau ergibt ein vollflächiges Icon.
    writeFileSync(join(publicDir, 'apple-touch-icon.png'), await png(tile, 180, primary));
  } finally {
    await browser.close();
  }
  console.log(`Icons in ${publicDir} erzeugt (Browser: ${browserPath}).`);
} finally {
  rmSync(workDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
}
