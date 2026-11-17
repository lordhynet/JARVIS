// AudioContext is explicitly configured for 24kHz mono. Audio never enters local storage.
class JarvisPcm extends AudioWorkletProcessor {
  constructor() { super(); this.samples = new Int16Array(480); this.offset = 0; }
  process(inputs, outputs) {
    for (const channel of outputs[0] || []) channel.fill(0);
    const data = inputs[0]?.[0]; if (!data) return true;
    for (const value of data) {
      this.samples[this.offset++] = Math.round(Math.max(-1, Math.min(1, value)) * 32767);
      if (this.offset === this.samples.length) { const buffer = this.samples.buffer; this.port.postMessage(buffer, [buffer]); this.samples = new Int16Array(480); this.offset = 0; }
    }
    return true;
  }
}
registerProcessor('jarvis-pcm', JarvisPcm);
