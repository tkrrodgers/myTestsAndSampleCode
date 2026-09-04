# POC Interactive Training Client

Connects the local Blazor OKF lesson to models exposed through the VS Code Language Model API.

## Use

1. Start the lesson server at `http://127.0.0.1:5000`.
2. Copy the bridge token shown in the Blazor UI.
3. Run **POC Training: Connect** and paste the token.
4. Start the lesson in Blazor.
5. Confirm each model request in VS Code.

The extension never invokes a model silently. The bridge token is held in VS Code SecretStorage.

## Models

- Coach: `google/gemma-4-31B-it via novita`
- Availability fallback: `GPT-5.6 Sol`
- Independent feedback: `Claude Opus 5`

Fallback is attempted only when Gemma is unavailable, denied, times out, errors, or returns no text. Claude review has no cross-model fallback.

## Commands

- **POC Training: Connect**
- **POC Training: Disconnect**
- **POC Training: Open Blazor Lesson**