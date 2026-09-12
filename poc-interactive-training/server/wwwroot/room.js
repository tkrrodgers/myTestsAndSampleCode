// "LLM in the Room": microphone capture and spoken answers. Audio never leaves the machine — the WAV is
// posted to the loopback server, which hands it to a local whisper.cpp process.
window.roomMic = {
    _ctx: null, _stream: null, _proc: null, _chunks: [], _recording: false, _startedAt: 0,

    supported: function () {
        return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && (window.AudioContext || window.webkitAudioContext));
    },

    // Captures mono float samples. whisper.cpp wants 16 kHz; most browsers honour the requested rate,
    // and stop() resamples if this one did not.
    start: async function () {
        if (this._recording) { return this._ctx.sampleRate; }
        this._stream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }
        });
        const Ctx = window.AudioContext || window.webkitAudioContext;
        try { this._ctx = new Ctx({ sampleRate: 16000 }); } catch { this._ctx = new Ctx(); }
        const source = this._ctx.createMediaStreamSource(this._stream);
        this._proc = this._ctx.createScriptProcessor(4096, 1, 1);
        this._chunks = [];
        const self = this;
        this._proc.onaudioprocess = function (e) {
            if (self._recording) { self._chunks.push(new Float32Array(e.inputBuffer.getChannelData(0))); }
        };
        // A processor only runs when connected to the graph; a muted gain keeps the mic out of the speakers.
        const mute = this._ctx.createGain();
        mute.gain.value = 0;
        source.connect(this._proc);
        this._proc.connect(mute);
        mute.connect(this._ctx.destination);
        this._recording = true;
        this._startedAt = performance.now();
        return this._ctx.sampleRate;
    },

    // Stops capture, encodes 16-bit PCM WAV and returns the transcript from the local whisper server.
    stop: async function () {
        if (!this._recording) { return { text: '', sttMs: 0, seconds: 0 }; }
        this._recording = false;
        const seconds = (performance.now() - this._startedAt) / 1000;
        const rate = this._ctx.sampleRate;
        const chunks = this._chunks;
        this._teardown();

        let total = 0;
        chunks.forEach(c => total += c.length);
        let samples = new Float32Array(total);
        let offset = 0;
        chunks.forEach(c => { samples.set(c, offset); offset += c.length; });
        if (rate !== 16000) { samples = this._resample(samples, rate, 16000); }
        if (samples.length < 16000 * 0.4) { return { text: '', sttMs: 0, seconds: seconds }; }

        const wav = this._encodeWav(samples, 16000);
        const t0 = performance.now();
        const response = await fetch('/api/room/transcribe', { method: 'POST', headers: { 'Content-Type': 'audio/wav' }, body: wav });
        const sttMs = Math.round(performance.now() - t0);
        if (!response.ok) { return { text: '', sttMs: sttMs, seconds: seconds, error: 'transcription failed (' + response.status + ')' }; }
        const json = await response.json();
        return { text: json.text || '', sttMs: sttMs, seconds: seconds };
    },

    cancel: function () {
        this._recording = false;
        this._chunks = [];
        this._teardown();
    },

    _teardown: function () {
        try { this._proc && this._proc.disconnect(); } catch { }
        try { this._stream && this._stream.getTracks().forEach(t => t.stop()); } catch { }
        try { this._ctx && this._ctx.close(); } catch { }
        this._proc = null; this._stream = null; this._ctx = null;
    },

    _resample: function (input, from, to) {
        const ratio = from / to;
        const length = Math.round(input.length / ratio);
        const output = new Float32Array(length);
        for (let i = 0; i < length; i++) {
            const position = i * ratio;
            const index = Math.floor(position);
            const next = Math.min(index + 1, input.length - 1);
            const frac = position - index;
            output[i] = input[index] * (1 - frac) + input[next] * frac;
        }
        return output;
    },

    _encodeWav: function (samples, rate) {
        const buffer = new ArrayBuffer(44 + samples.length * 2);
        const view = new DataView(buffer);
        const write = (o, s) => { for (let i = 0; i < s.length; i++) view.setUint8(o + i, s.charCodeAt(i)); };
        write(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); write(8, 'WAVE');
        write(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
        view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
        write(36, 'data'); view.setUint32(40, samples.length * 2, true);
        let o = 44;
        for (let i = 0; i < samples.length; i++, o += 2) {
            const s = Math.max(-1, Math.min(1, samples[i]));
            view.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
        }
        return buffer;
    }
};

// Sentence-at-a-time speech so the room hears the first sentence while the rest is still generating.
// Unlike trainingSpeech.speak this must not cancel the utterance already playing.
window.roomSpeech = {
    _queue: [], _busy: false, _last: '',

    enqueue: function (text) {
        if (!('speechSynthesis' in window) || !text) { return; }
        // The same sentence arriving twice in a row is a duplicate event, not a repeated answer.
        if (text === this._last) { return; }
        this._last = text;
        this._queue.push(text);
        this._next();
    },

    stop: function () {
        this._queue = [];
        this._busy = false;
        this._last = '';
        if ('speechSynthesis' in window) { window.speechSynthesis.cancel(); }
    },

    _next: function () {
        if (this._busy || this._queue.length === 0) { return; }
        const text = this._queue.shift();
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.rate = 1.02;
        const self = this;
        const done = function () { self._busy = false; self._next(); };
        utterance.onend = done;
        utterance.onerror = done;
        this._busy = true;
        window.speechSynthesis.speak(utterance);
        window.speechSynthesis.resume();
    }
};
