window.trainingSpeech = {
    // Observes whether speech actually started rather than inferring it from feature detection.
    // Two Chromium behaviours have to be worked around or narration is silently dead:
    //   - speak() issued in the same task as cancel() leaves the engine stuck (speaking=true, no events)
    //   - utterances past roughly 15s are dropped unless the queue is nudged
    speak: function (text) {
        if (!('speechSynthesis' in window)) { return Promise.resolve('unsupported'); }
        if (!text) { return Promise.resolve('empty'); }

        const synth = window.speechSynthesis;
        synth.cancel();

        return new Promise(function (resolve) {
            let settled = false;
            let keepAlive = 0;

            const settle = function (value) {
                if (!settled) { settled = true; resolve(value); }
            };

            const stopKeepAlive = function () {
                if (keepAlive) { clearInterval(keepAlive); keepAlive = 0; }
            };

            setTimeout(function () {
                const utterance = new SpeechSynthesisUtterance(text);
                utterance.rate = 0.96;
                utterance.pitch = 1;
                utterance.onstart = function () { settle('speaking'); };
                utterance.onend = stopKeepAlive;
                utterance.onerror = function (event) {
                    stopKeepAlive();
                    settle('error:' + (event.error || 'unknown'));
                };

                synth.speak(utterance);
                synth.resume();

                keepAlive = setInterval(function () {
                    if (!synth.speaking) { stopKeepAlive(); return; }
                    synth.pause();
                    synth.resume();
                }, 10000);
            }, 150);

            setTimeout(function () {
                if (synth.getVoices().length === 0) { settle('no-voices'); return; }
                settle(synth.speaking || synth.pending ? 'stalled' : 'silent');
            }, 5000);
        });
    },
    stop: function () {
        if ('speechSynthesis' in window) {
            window.speechSynthesis.cancel();
        }
    },
    copy: async function (text) {
        await navigator.clipboard.writeText(text);
    },
    focus: function (id) {
        document.getElementById(id)?.focus();
    },
    scrollToTop: function () {
        window.scrollTo({ top: 0, behavior: 'auto' });
        document.scrollingElement?.scrollTo({ top: 0, behavior: 'auto' });
        document.querySelector('.stage')?.scrollTo({ top: 0, behavior: 'auto' });
    },
    // Follows streamed output only while the reader is already at the bottom, so scrolling back to
    // re-read an earlier step is not yanked away by the next chunk.
    stickToBottom: function (id) {
        const el = document.getElementById(id);
        if (!el) { return; }
        const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
        if (distance < 120) {
            el.scrollTop = el.scrollHeight;
        }
    }
};