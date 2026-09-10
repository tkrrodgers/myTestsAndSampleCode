// Autopilot driver. Dispatches real DOM events on real controls so the demo can only show a path a
// learner could actually take; a broken button breaks the run, which is the correct outcome.
window.autopilot = {
    highlight: function (selector) {
        document.querySelectorAll('.autopilot-target').forEach(el => el.classList.remove('autopilot-target'));
        const el = document.querySelector(selector);
        if (!el) { return false; }
        el.classList.add('autopilot-target');
        el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        return true;
    },

    exists: function (selector) {
        return !!document.querySelector(selector);
    },

    enabled: function (selector) {
        const el = document.querySelector(selector);
        return !!el && !el.disabled;
    },

    click: function (selector) {
        const el = document.querySelector(selector);
        if (!el || el.disabled) { return false; }
        el.click();
        return true;
    },

    // Blazor binds on change/input, so the event must be dispatched or the binding never fires.
    setValue: function (selector, value) {
        const el = document.querySelector(selector);
        if (!el || el.disabled) { return false; }
        const setter = Object.getOwnPropertyDescriptor(
            el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLTextAreaElement.prototype,
            'value')?.set;
        if (setter) { setter.call(el, value); } else { el.value = value; }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    },

    scrollTo: function (selector) {
        const el = document.querySelector(selector);
        if (!el) { return false; }
        el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        return true;
    },

    clear: function () {
        document.querySelectorAll('.autopilot-target').forEach(el => el.classList.remove('autopilot-target'));
    },

    // Returns whether audio actually started. An auto-run that appears to narrate but is silent is
    // worse than one that says it cannot. Same Chromium workarounds as trainingSpeech.speak.
    speak: function (text, rate) {
        if (!window.speechSynthesis || !text) { return Promise.resolve('unsupported'); }

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
                utterance.rate = rate || 1;
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

    stopSpeaking: function () {
        window.speechSynthesis?.cancel();
    },

    // Escape must always end a run; a demo you cannot stop is a demo you cannot show.
    registerAbort: function (dotNetRef) {
        if (window.__autopilotAbort) {
            document.removeEventListener('keydown', window.__autopilotAbort);
        }
        window.__autopilotAbort = function (event) {
            if (event.key === 'Escape') { dotNetRef.invokeMethodAsync('AbortFromKeyboard'); }
        };
        document.addEventListener('keydown', window.__autopilotAbort);
    }
};
