// Use public Media Session and MusicKit playback controls only. Never read account, licence or provider internals.
(() => {
  const allowed = new Set(["play", "pause", "nexttrack", "previoustrack", "seekto", "shuffle", "repeat", "volume"]);
  const handlers = new Map();
  const session = navigator.mediaSession;
  let positionState, lastReport;
  const post = data => window.postMessage({ source: "fuplayer-page-controls", ...data }, location.origin);
  function player() {
    if (location.hostname !== "music.apple.com") return null;
    try {
      const instance = globalThis.MusicKit?.getInstance();
      // MusicKit 3 exposes these controls on the instance; version 1 exposes them on player.
      return instance && ("volume" in instance ? instance : instance.player);
    } catch { return null; }
  }
  function metadata(music) {
    const item = music?.nowPlayingItem?.attributes;
    const media = session?.metadata;
    const text = value => typeof value === "string" ? value.slice(0, 512).trim() : "";
    const result = { title: text(item?.name) || text(media?.title), artist: text(item?.artistName) || text(media?.artist),
      album: text(item?.albumName) || text(media?.album), genre: Array.isArray(item?.genreNames) ? item.genreNames.slice(0, 4).map(text).join(" / ").slice(0, 256) : "" };
    const image = item?.artwork?.url || media?.artwork?.[0]?.src;
    if (typeof image === "string" && image.length <= 4096) {
      try {
        const url = new URL(image.replaceAll("{w}", "512").replaceAll("{h}", "512").replaceAll("{f}", "jpg"), location.href);
        if (["https:", "http:"].includes(url.protocol) && !url.username && !url.password && url.href.length <= 4096) result.artwork = url.href;
      } catch { }
    }
    return result.title || result.artist || result.album ? result : null;
  }
  function report(force = false) {
    try {
    const music = player(), actions = new Set(handlers.keys());
    const transport = {};
    if (music) {
      if (typeof music.seekToTime === "function") actions.add("seekto");
      if ("shuffleMode" in music) { actions.add("shuffle"); transport.shuffle = music.shuffleMode === 1; }
      if ("repeatMode" in music) {
        actions.add("repeat");
        transport.repeat = music.repeatMode === 1 ? "one" : music.repeatMode === 2 ? "all" : "off";
      }
      if (Number.isFinite(music.volume)) { actions.add("volume"); transport.volume = music.volume; }
      transport.duration = music.currentPlaybackDuration;
      transport.position = music.currentPlaybackTime;
    } else if (positionState) Object.assign(transport, positionState);
    const result = { actions: [...actions], ...(Object.keys(transport).length ? { transport } : {}), metadata: metadata(music) };
    const serialised = JSON.stringify(result);
    if (force || serialised !== lastReport) { lastReport = serialised; post(result); }
    } catch { /* Optional SDK state must never change the provider's native registration behaviour. */ }
  }
  if (globalThis.MediaSession && session) {
    const original = MediaSession.prototype.setActionHandler;
    MediaSession.prototype.setActionHandler = function(action, handler) {
      const result = Reflect.apply(original, this, [action, handler]);
      if (this === session && allowed.has(action)) {
        if (handler) handlers.set(action, handler); else handlers.delete(action);
        report();
      }
      return result;
    };
    const originalPosition = MediaSession.prototype.setPositionState;
    if (originalPosition) MediaSession.prototype.setPositionState = function(state) {
      const result = Reflect.apply(originalPosition, this, [state]);
      if (this === session) { positionState = state ? { duration: state.duration, position: state.position ?? 0 } : undefined; report(); }
      return result;
    };
  }
  window.addEventListener("message", event => {
    if (event.source !== window || event.origin !== location.origin || event.data?.source !== "fuplayer-content-controls") return;
    const { action, value } = event.data;
    if (action === "query") { report(true); return; }
    if (!allowed.has(action)) return;
    const music = player();
    Promise.resolve().then(() => {
      if (action === "volume" && music && Number.isFinite(value) && value >= 0 && value <= 1) music.volume = value;
      else if (action === "shuffle" && music && typeof value === "boolean") music.shuffleMode = value ? 1 : 0;
      else if (action === "repeat" && music && ["off", "all", "one"].includes(value)) music.repeatMode = value === "one" ? 1 : value === "all" ? 2 : 0;
      else if (action === "seekto" && Number.isFinite(value) && value >= 0) {
        if (handlers.has(action)) return handlers.get(action)({ action, seekTime: value, fastSeek: false });
        if (music && typeof music.seekToTime === "function") return music.seekToTime(value);
      } else if (handlers.has(action)) return handlers.get(action)({ action });
    }).then(() => report(true)).catch(error => post({ controlError: error.name || "Error" }));
  });
  // Reading only documented playback properties also observes service-side volume, shuffle and repeat changes.
  setInterval(report, 500);
  report();
})();
