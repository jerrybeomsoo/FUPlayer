// The signed bundle is fixed. Native messaging supplies session data, never executable code.
(async () => {
const FUPlayerBridge = await FUPlayerBridgeReady;
if (!FUPlayerBridge) return;
browser.permissions?.onRemoved?.addListener(change => {
  if (change.data_collection?.includes("technicalAndInteraction")) FUPlayerBridge.reportDiagnostics = false;
});
browser.permissions?.onAdded?.addListener(change => {
  if (change.data_collection?.includes("technicalAndInteraction")) FUPlayerBridge.reportDiagnostics = true;
});
const ports = new Set();
const socket = new WebSocket(FUPlayerBridge.socket);
let connected = false, hostViewport, serviceWindow, serviceTab, serviceOpened = false, windowReady = false;
function openService() {
  if (!windowReady || !Number.isInteger(serviceTab) || serviceOpened) return;
  serviceOpened = true;
  browser.tabs.update(serviceTab, { url: FUPlayerBridge.address }).catch(() => {
    socket.send(JSON.stringify({ error: "Firefox could not open the selected service." }));
  });
}
async function markServiceWindow() {
  if (Number.isInteger(serviceWindow))
    // Unlike create(), Firefox's update() explicitly refreshes the native titlebar after setting the preface.
    await browser.windows.update(serviceWindow, { titlePreface: FUPlayerBridge.windowTitleMarker });
}
browser.tabs.onUpdated.addListener((_id, change, tab) => {
  if (tab.windowId === serviceWindow && (change.status === "complete" || typeof change.title === "string"))
    markServiceWindow().catch(() => {});
});
function fit(tab) {
  if (!Number.isInteger(tab) || !hostViewport) return;
  const zoom = Math.max(0.5, Math.min(1, hostViewport.width / 1120, hostViewport.height / 760));
  browser.tabs.setZoom(tab, zoom).catch(() => {});
}
browser.runtime.onConnect.addListener(port => {
  if (port.name !== "pcm" || !port.sender?.url || new URL(port.sender.url).origin !== FUPlayerBridge.origin) {
    port.disconnect(); return;
  }
  ports.add(port);
  fit(port.sender.tab?.id);
  port.postMessage({ connected });
  port.onDisconnect.addListener(() => ports.delete(port));
  port.onMessage.addListener(message => {
    if (!connected) return;
    if (message.error) {
      socket.send(JSON.stringify({ error: FUPlayerBridge.reportDiagnostics === false
        ? "Browser direct audio stopped." : String(message.error).slice(0, 240) }));
      return;
    }
    if (message.state === "playing" || message.state === "paused" || message.state === "stopped") { socket.send(JSON.stringify({ state: message.state })); return; }
    if (message.audio) { if (FUPlayerBridge.reportDiagnostics !== false) socket.send(JSON.stringify({ audio: message.audio })); return; }
    if (message.transport) { socket.send(JSON.stringify({ transport: message.transport })); return; }
    if (Object.hasOwn(message, "metadata")) { socket.send(JSON.stringify({ metadata: message.metadata })); return; }
    if (message.discontinuity === true) { socket.send(JSON.stringify({ discontinuity: true })); return; }
    if (Array.isArray(message.actions)) { socket.send(JSON.stringify({ actions: message.actions })); return; }
    if (message.controlError) { socket.send(JSON.stringify({ controlError: FUPlayerBridge.reportDiagnostics === false
      ? "Control failed." : String(message.controlError).slice(0, 120) })); return; }
    if (!(message.pcm instanceof ArrayBuffer) || message.pcm.byteLength !== 16384) return;
    if (socket.bufferedAmount > 262144) {
      socket.close(1008, "FUPlayer is not keeping up"); return;
    }
    socket.send(message.pcm);
  });
});
socket.onmessage = event => {
  try {
    const message = JSON.parse(event.data);
    if (message.action === "windowready") {
      // The player has found this known-title window (and attached it when requested) before provider navigation.
      windowReady = true;
      openService();
    }
    if (["play", "pause", "stop", "nexttrack", "previoustrack", "seekto", "volume", "shuffle", "repeat"].includes(message.action))
      for (const port of ports) port.postMessage({ action: message.action, value: message.value });
    if (message.action === "viewport" && Number.isFinite(message.width) && Number.isFinite(message.height)) {
      // Fit the service's full player chrome to the host; longer catalogues retain their own normal scrolling.
      hostViewport = { width: message.width, height: message.height };
      for (const tab of new Set([...ports].map(port => port.sender.tab?.id).filter(id => Number.isInteger(id))))
        fit(tab);
    }
  } catch { }
};
socket.onopen = async () => {
  connected = true;
  socket.send(JSON.stringify({ addon: { mode: FUPlayerBridge.mode || "temporary", version: "0.3.1" } }));
  for (const port of ports) port.postMessage({ connected });
  // Kiosk main windows are forced fullscreen. A normal popup has no browser chrome and can reflow to the host.
  const setup = (await browser.tabs.query({})).filter(tab =>
    tab.url === FUPlayerBridge.startupAddress || (!FUPlayerBridge.startupAddress && tab.url?.startsWith("about:debugging")));
  if (setup.length) {
    try {
      const created = await browser.windows.create({ type: "popup", url: browser.runtime.getURL("host.html"),
        state: "minimized", focused: false, titlePreface: FUPlayerBridge.windowTitleMarker });
      serviceWindow = created.id;
      serviceTab = created.tabs?.[0]?.id;
      if (!Number.isInteger(serviceTab)) throw new Error("Missing service tab");
      await markServiceWindow();
      openService();
      for (const tab of setup) await browser.tabs.remove(tab.id);
    } catch {
      socket.send(JSON.stringify({ error: "Firefox could not create its resizable service window." }));
    }
  }
};
socket.onclose = socket.onerror = () => {
  connected = false;
  for (const port of ports) port.postMessage({ connected });
};
})();
