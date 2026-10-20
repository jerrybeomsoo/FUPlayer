// No audio output: the worklet exports float PCM and never connects it to speakers.
class FUPlayerPcm extends AudioWorkletProcessor {
  constructor() {
    super();
    this.samples = new Float32Array(2048 * 2);
    this.frames = 0;
    this.port.onmessage = event => { if (event.data?.reset) this.frames = 0; };
  }
  process(inputs) {
    const input = inputs[0];
    if (!input || !input[0]) return true;
    const left = input[0], right = input[1] || left;
    for (let i = 0; i < left.length; i++) {
      this.samples[this.frames * 2] = left[i];
      this.samples[this.frames * 2 + 1] = right[i];
      if (++this.frames === 2048) {
        this.port.postMessage(this.samples.buffer, [this.samples.buffer]);
        this.samples = new Float32Array(2048 * 2);
        this.frames = 0;
      }
    }
    return true;
  }
}
registerProcessor("fuplayer-pcm", FUPlayerPcm);
