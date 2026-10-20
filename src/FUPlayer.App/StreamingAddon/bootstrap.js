// Fixed signed code: each launch obtains only the running player's session through Firefox's allowlisted native host.
const FUPlayerBridgeReady = (async () => {
  const value = await browser.runtime.sendNativeMessage("com.fuplayer.streaming", { type: "session" });
  if (!value || value.error) throw new Error("FUPlayer has no active streaming session.");
  const socket = new URL(value.socket), address = new URL(value.address);
  if (socket.protocol !== "ws:" || socket.hostname !== "127.0.0.1" || !socket.port || socket.username || socket.password
      || socket.hash || socket.search || !/^\/[a-f0-9]{64}\/audio$/.test(socket.pathname)
      || address.protocol !== "https:" || address.hostname !== "music.apple.com" || address.username || address.password
      || value.origin !== address.origin || !/^FUPlayer Streaming [a-f0-9]{32} \| $/.test(value.windowTitleMarker))
    throw new Error("Invalid FUPlayer streaming session.");
  let reportDiagnostics = false;
  try { reportDiagnostics = (await browser.permissions.getAll()).data_collection?.includes("technicalAndInteraction") === true; } catch { }
  return { socket: socket.href, address: address.href, origin: address.origin, windowTitleMarker: value.windowTitleMarker,
    startupAddress: value.startupAddress, mode: "persistent", reportDiagnostics };
})().catch(() => null);
