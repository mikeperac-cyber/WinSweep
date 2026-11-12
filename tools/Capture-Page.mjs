// Captures a full-page screenshot through the Chrome DevTools Protocol.
//
// Chrome clamps its own window width on Windows (about 500px), so a 390px mobile
// frame cannot be taken with --screenshot. Driving the protocol lets the page be
// emulated at an exact width and captured beyond the viewport instead.
//
//   node tools/Capture-Page.mjs --url http://127.0.0.1:4173/ --width 390 --out out.png [--mobile]
//
// Requires Chrome already running with --remote-debugging-port=9222.

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(`--${name}`);
  return index === -1 ? fallback : args[index + 1];
};

const url = option("url", "http://127.0.0.1:4173/");
const width = Number(option("width", "1440"));
const height = Number(option("height", "900"));
const out = option("out", "page.png");
const mobile = args.includes("--mobile");
const settle = Number(option("settle", "900"));
const port = option("port", "9222");

const fs = await import("node:fs/promises");

async function openTarget(host) {
  const created = await fetch(`http://${host}:${port}/json/new?about:blank`, { method: "PUT" })
    .then((response) => (response.ok ? response.json() : null))
    .catch(() => null);

  if (created?.webSocketDebuggerUrl) return created;

  // Older builds refuse PUT; a tab that already exists works just as well.
  const list = await fetch(`http://${host}:${port}/json/list`)
    .then((response) => (response.ok ? response.json() : null))
    .catch(() => null);

  return (list ?? []).find((entry) => entry.type === "page") ?? null;
}

let target = await openTarget("127.0.0.1");
if (!target?.webSocketDebuggerUrl) target = await openTarget("[::1]");

if (!target?.webSocketDebuggerUrl) {
  console.error("Could not open a DevTools target. Is Chrome running with --remote-debugging-port?");
  process.exit(1);
}

const socket = new WebSocket(target.webSocketDebuggerUrl);
const pending = new Map();
let nextId = 1;

const send = (method, params = {}) =>
  new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });

socket.addEventListener("message", (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    const { resolve, reject } = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) reject(new Error(`${message.error.message} (${message.error.code})`));
    else resolve(message.result);
  }
});

await new Promise((resolve, reject) => {
  socket.addEventListener("open", resolve, { once: true });
  socket.addEventListener("error", reject, { once: true });
});

await send("Page.enable");
await send("Emulation.setDeviceMetricsOverride", {
  width,
  height,
  deviceScaleFactor: 1,
  mobile,
  screenWidth: width,
  screenHeight: height
});

await send("Page.navigate", { url });
await new Promise((resolve) => setTimeout(resolve, settle));

// Make sure fonts and images are in before measuring the page.
await send("Runtime.evaluate", { expression: "document.fonts.ready", awaitPromise: true });
await new Promise((resolve) => setTimeout(resolve, 250));

const metrics = await send("Page.getLayoutMetrics");
const content = metrics.cssContentSize ?? metrics.contentSize;
const fullHeight = Math.ceil(content.height);
const fullWidth = Math.ceil(content.width);

const shot = await send("Page.captureScreenshot", {
  format: "png",
  captureBeyondViewport: true,
  clip: { x: 0, y: 0, width: fullWidth, height: fullHeight, scale: 1 }
});

await fs.writeFile(out, Buffer.from(shot.data, "base64"));
console.log(`${out}  ${fullWidth} x ${fullHeight}  (viewport ${width} x ${height}, mobile: ${mobile})`);

const base = target.webSocketDebuggerUrl.includes("[::1]") ? "[::1]" : "127.0.0.1";
await fetch(`http://${base}:${port}/json/close/${target.id}`).catch(() => {});
socket.close();
process.exit(0);
