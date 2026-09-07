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

    speak: function (text, rate) {
        if (!window.speechSynthesis) { return; }
        window.speechSynthesis.cancel();
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.rate = rate || 1;
        window.speechSynthesis.speak(utterance);
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
