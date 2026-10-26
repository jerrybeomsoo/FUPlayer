// Use standard media/Web Audio interfaces only. Firefox's CORS and protected-media checks stay in force.
(() => {
  const port = browser.runtime.connect({ name: "pcm" });
  const elements = new Set(), sources = new Map();
  const playing = new WeakSet(), attempted = new WeakSet();
  const pageActions = new Set(), resumeTargets = new Set();
  let context, sink, initialising, connected = false, pending = Promise.resolve(), lastAudioStatus, pageTimeline = false, pageVolume = false;
  let lastTimeline, requestedSeek, serviceState, lastMediaState, clock;
  const activeMedia = () => [...sources.keys()].find(element => !element.paused && !element.ended)
    || [...resumeTargets][0] || [...sources.keys()].at(-1);
  function discontinuity() {
    if (!connected || !sources.size) return;
    sink?.port.postMessage({ reset: true });
    port.postMessage({ discontinuity: true });
  }
  function timeline(value) {
    if (!Number.isFinite(value.position) || !Number.isFinite(value.duration)) return;
    const now = performance.now(), previous = lastTimeline;
    const requested = requestedSeek && now - requestedSeek.at < 5000;
    // The public track clock is authoritative. Providers can seek segment/preview elements internally.
    const acknowledged = requested && Math.abs(value.position - requestedSeek.position) < 1;
    const changedTrack = previous?.trackId && value.trackId && previous.trackId !== value.trackId;
    const naturalRepeat = previous && previous.duration - previous.position < 3 && value.position < 1;
    const jumped = previous && !changedTrack && !naturalRepeat && value.duration === previous.duration
      && (value.position < previous.position - 0.75
        || value.position > previous.position + (now - previous.at) / 1000 + 1.5);
    if (acknowledged || (!requested && jumped)) { discontinuity(); requestedSeek = undefined; }
    else if (requestedSeek && !requested) requestedSeek = undefined;
    lastTimeline = { position: value.position, duration: value.duration, trackId: value.trackId, at: now };
    pageTimeline = true;
  }
  function reportTransport() {
    if (!connected) return;
    const element = activeMedia();
    if (!element) return;
    const transport = {};
    if (!pageTimeline) { transport.position = element.currentTime; transport.duration = Number.isFinite(element.duration) ? element.duration : 0; }
    if (!pageVolume) transport.volume = element.muted ? 0 : element.volume;
    if (Object.keys(transport).length) port.postMessage({ transport });
  }
  function reportActions() {
    const actions = new Set(pageActions);
    const element = activeMedia();
    if (element) { actions.add("volume"); if (Number.isFinite(element.duration) && element.duration > 0) actions.add("seekto"); }
    port.postMessage({ actions: [...actions] });
  }
  function reportAudio() {
    if (!connected) return;
    const audio = { context: context?.state || "none", watched: elements.size, captured: sources.size };
    const status = JSON.stringify(audio);
    if (status !== lastAudioStatus) { lastAudioStatus = status; port.postMessage({ audio }); }
  }
  function ensureContext() {
    if (!context) {
      context = new AudioContext({ sampleRate: 48000, latencyHint: "playback" });
      context.addEventListener("statechange", reportAudio);
    }
    return context;
  }
  function resume() {
    // Autoplay can leave this promise pending until a trusted browser gesture. Never block future attachments on it.
    ensureContext().resume().then(reportAudio, error => failure("resume", error));
    reportAudio();
  }
  function initialise() {
    return initialising ||= (async () => {
      ensureContext();
      await context.audioWorklet.addModule(browser.runtime.getURL("worklet.js"));
      sink = new AudioWorkletNode(context, "fuplayer-pcm", {
        numberOfInputs: 1, numberOfOutputs: 0, channelCount: 2, channelCountMode: "explicit"
      });
      sink.port.onmessage = event => {
        if (connected && serviceState !== "paused" && serviceState !== "stopped") port.postMessage({ pcm: event.data });
      };
      // An output-free zero source keeps the PCM clock alive while a provider swaps media elements.
      clock = context.createConstantSource();
      clock.offset.value = 0;
      clock.connect(sink);
      clock.start();
      sink.onprocessorerror = () => failure("worklet processing", { name: "ProcessorError" });
      // Neither the source nor the output-free sink connects to context.destination.
      // Suspending an unstarted context can wait indefinitely. It has no speaker connection to silence here.
    })();
  }
  function suspend() { context?.suspend().catch(() => {}); }
  function reportState() {
    if (!sources.size) return;
    const state = serviceState || ([...sources.keys()].some(element => !element.paused && !element.ended) ? "playing" : "paused");
    if (state !== lastMediaState) {
      lastMediaState = state;
      try { port.postMessage({ state }); } catch { }
    }
  }
  function failure(stage, error) {
    suspend();
    // Keep the provider's own playback/licence lifecycle intact. Native output stays silent in this profile.
    try { port.postMessage({ error: `Direct Web Audio ${stage}: ${error.name || "Error"}.` }); } catch { }
  }
  async function attach(element) {
    if (!connected || !playing.has(element) || element.paused || element.ended) return;
    if (!attempted.has(element)) {
      attempted.add(element);
      try { await initialise(); } catch (error) { failure("worklet setup", error); return; }
      try {
        const source = context.createMediaElementSource(element);
        source.connect(sink);
        sources.set(element, source);
        reportAudio();
      } catch (error) { failure("media source", error); return; }
    }
    if (sources.has(element)) {
      reportState();
      reportActions();
      reportTransport();
      resume();
    }
  }
  function schedule(element) {
    pending = pending.then(() => attach(element)).catch(error => failure("attachment", error));
  }
  function pauseIfIdle() {
    // A paused/ended segment is not the lifetime of the service's track or audio context.
    // Keep the graph warm; explicit provider pause suppresses PCM without closing its connection.
    reportState();
  }
  function watch(element) {
    if (elements.has(element)) return;
    elements.add(element);
    // Creating a source during DOM discovery makes Firefox reject later setMediaKeys calls.
    // Wait until the provider and browser have begun ordinary playback; never intercept or alter EME calls.
    element.addEventListener("playing", () => { playing.add(element); schedule(element); });
    // A service can begin playback before inserting its element into the observed DOM.
    // Advancing media time recovers a missed playing event without capturing during licence setup or early play.
    let lastTime = element.currentTime;
    element.addEventListener("timeupdate", () => {
      const time = element.currentTime;
      if (time > lastTime && !element.paused && !element.ended && !element.seeking && element.readyState >= 2) {
        playing.add(element);
        if (!sources.has(element) || context?.state !== "running") schedule(element);
      }
      lastTime = time;
      reportTransport();
    });
    element.addEventListener("durationchange", () => { reportActions(); reportTransport(); });
    element.addEventListener("volumechange", reportTransport);
    element.addEventListener("seeking", () => {
      if (!sources.has(element) || !connected || pageTimeline || element !== activeMedia()) return;
      discontinuity(); requestedSeek = undefined;
    });
    element.addEventListener("pause", pauseIfIdle);
    element.addEventListener("ended", pauseIfIdle);
    reportAudio();
    recover(element);
  }
  function recover(element) {
    // A reused element can already have played before it becomes observable. Played ranges confirm
    // ordinary playback without waiting for a clock that a suspended context itself may have stopped.
    if (!connected || element.paused || element.ended || element.seeking || element.readyState < 2) return;
    if (!playing.has(element) && !(element.played?.length > 0)) return;
    playing.add(element);
    if (!sources.has(element) || context?.state !== "running") schedule(element);
  }
  function scan(root) {
    if (root instanceof HTMLMediaElement) watch(root);
    for (const element of root.querySelectorAll("audio,video")) watch(element);
    for (const element of root.querySelectorAll("*")) if (element.shadowRoot) scan(element.shadowRoot);
  }
  port.onMessage.addListener(message => {
    if (message.action) {
      const action = message.action;
      if (action === "seekto" && Number.isFinite(message.value))
        requestedSeek = { position: message.value, at: performance.now() };
      if (["play", "pause", "stop"].includes(action)) {
        serviceState = action === "play" ? "playing" : action === "stop" ? "stopped" : "paused";
        if (action === "play") resume();
      }
      if (action === "pause" || action === "stop") {
        resumeTargets.clear();
        for (const element of sources.keys()) if (!element.paused && !element.ended) resumeTargets.add(element);
      }
      if (pageActions.has(action)) {
        window.postMessage({ source: "fuplayer-content-controls", action, value: message.value }, location.origin);
      } else if (action === "pause" || action === "stop") {
        for (const element of resumeTargets) { element.pause(); if (action === "stop") element.currentTime = 0; }
        pauseIfIdle();
      } else if (action === "play") {
        const targets = resumeTargets.size ? resumeTargets : sources.keys();
        for (const element of targets) element.play().catch(error => {
          try { port.postMessage({ controlError: error.name || "Error" }); } catch { }
        });
      }
      if (action === "seekto" && !pageActions.has(action) && Number.isFinite(message.value)) {
        const element = activeMedia();
        if (element && Number.isFinite(element.duration)) element.currentTime = Math.max(0, Math.min(element.duration, message.value));
      } else if (action === "volume" && !pageActions.has(action) && Number.isFinite(message.value) && message.value >= 0 && message.value <= 1) {
        for (const element of sources.keys()) { element.volume = message.value; element.muted = message.value === 0; }
        reportTransport();
      }
      reportState();
      return;
    }
    connected = message.connected === true;
    if (connected) {
      lastAudioStatus = undefined;
      reportAudio();
      window.postMessage({ source: "fuplayer-content-controls", action: "query" }, location.origin);
      for (const element of elements) recover(element);
    } else {
      for (const element of sources.keys()) element.pause();
      suspend();
    }
  });
  port.onDisconnect.addListener(() => {
    connected = false;
    for (const element of sources.keys()) element.pause();
    suspend();
  });
  // Resume synchronously inside the user's normal service click/key, before asynchronous worklet loading.
  // This follows Firefox's autoplay policy and does not attach a media source before ordinary playback.
  const activate = event => { if (connected && event.isTrusted) resume(); };
  window.addEventListener("pointerdown", activate, true);
  window.addEventListener("keydown", activate, true);
  // Also discover playback events when an existing element moves into the page or an open shadow root.
  document.addEventListener("playing", event => {
    if (event.target instanceof HTMLMediaElement) { watch(event.target); playing.add(event.target); schedule(event.target); }
  }, true);
  new MutationObserver(records => {
    for (const record of records) for (const node of record.addedNodes)
      if (node.nodeType === Node.ELEMENT_NODE) scan(node);
  }).observe(document, { childList: true, subtree: true });
  window.addEventListener("message", event => {
    if (event.source !== window || event.origin !== location.origin || event.data?.source !== "fuplayer-page-controls") return;
    if (Array.isArray(event.data.actions)) {
      pageActions.clear();
      for (const action of event.data.actions)
        if (["play", "pause", "stop", "nexttrack", "previoustrack", "seekto", "shuffle", "repeat", "volume"].includes(action)) pageActions.add(action);
      reportActions();
    }
    if (["playing", "paused", "stopped"].includes(event.data.playbackState)) {
      serviceState = event.data.playbackState;
      if (serviceState === "playing" && context?.state !== "running") resume();
      reportState();
    }
    if (event.data.transport) {
      const value = event.data.transport;
      timeline(value);
      if (Number.isFinite(value.volume)) pageVolume = true;
      port.postMessage({ transport: { position: value.position, duration: value.duration, volume: value.volume,
        shuffle: value.shuffle, repeat: value.repeat } });
    }
    if (Object.hasOwn(event.data, "metadata")) {
      const value = event.data.metadata;
      port.postMessage({ metadata: value ? { title: value.title, artist: value.artist, album: value.album,
        artwork: value.artwork, genre: value.genre } : null });
    }
    if (typeof event.data.controlError === "string") port.postMessage({ controlError: event.data.controlError });
  });
  window.postMessage({ source: "fuplayer-content-controls", action: "query" }, location.origin);
  scan(document);
  setInterval(() => {
    scan(document);
    for (const element of elements) recover(element);
    if (connected && sources.size && serviceState === "playing" && context?.state !== "running") resume();
  }, 500);
})();
