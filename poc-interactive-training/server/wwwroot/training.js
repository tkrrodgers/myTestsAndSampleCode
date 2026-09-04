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
    }
};