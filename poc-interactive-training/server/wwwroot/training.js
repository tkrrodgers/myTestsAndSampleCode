window.trainingSpeech = {
    speak: function (text) {
        if (!('speechSynthesis' in window) || !text) {
            return false;
        }

        window.speechSynthesis.cancel();
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.rate = 0.96;
        utterance.pitch = 1;
        window.speechSynthesis.speak(utterance);
        return true;
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