There is no public API call or DDE (Dynamic Data Exchange) function available to extensions or external scripts that can trigger or simulate the built-in "Copy All" action of the native VS Code Chat window.
VS Code’s core design strictly isolates the UI renderer process of the chat panel. Features like the "Copy All" button are internal parts of the private workbench layout, meaning they do not expose commands to the outer Extension API or system-level IPC mechanisms like DDE. (Note: DDE is an archaic Windows protocol that modern applications like VS Code, built on Electron and Node.js, do not use at all).
However, you can achieve the exact same result—getting the entire text content of the active chat window to your clipboard or into a variable—using one of these reliable workarounds:
## 1. The VS Code Core Command Approach (Keyboard Simulation)
While there isn't a direct code API for "Copy All", VS Code does expose commands to focus and select text in the chat pane. You can register an extension command or use an automation tool (like AutoHotkey or a macro) to chain these native keystroke commands together:

   1. workbench.action.chat.focus — Puts the focus inside the active chat window.
   2. editor.action.selectAll — Selects all text inside the focused panel.
   3. editor.action.clipboardCopyAction — Copies the selection to the system clipboard.

## 2. The Clean Extension API Approach (Recommended)
If you are writing a custom VS Code extension and want to capture the exact chat history without relying on buggy UI selection macros, you shouldn't try to click the UI button. Instead, read the structural data model directly using the proposed Chat API:

// Read the full structure of the active conversation programmaticallyconst activeSession = vscode.chat.activeSession; if (activeSession) {
    // Fetch every turn (user request and assistant response pairs)
    const history = vscode.chat.getSessionHistory(activeSession.sessionId);
    
    // Format it cleanly into a single string block
    const fullChatText = history.map(turn => {
        return `User: ${turn.prompt}\nAssistant: ${turn.response}`;
    }).join('\n\n');

    // Deterministically push the whole history block directly to the system clipboard
    await vscode.env.clipboard.writeText(fullChatText);
}

## 3. The Electron Debugger Approach (For External Scripts)
If you are writing an external script (like Python or Node.js) and cannot use an extension, you can launch VS Code with the debugging flag: code --remote-debugging-port=9222.

* You can then connect an automation framework like Playwright or Puppeteer to that port.
* Because the chat window is fundamentally just an HTML/CSS web view inside Electron, your script can deterministically query the DOM elements (e.g., matching .chat-message-container) and extract all text content out of the window instantly.

Are you writing a local script/macro to quickly grab your chat histories, or are you looking to integrate this into a custom VS Code extension feature?

