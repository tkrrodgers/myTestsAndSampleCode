import * as vscode from 'vscode';

interface BridgeTask {
	taskId: string;
	sessionId: string;
	kind: 'coach-narration' | 'claude-review' | 'model-answer' | 'model-judge' | 'gemma-story' | 'gemma-recreate' | 'claude-roundtrip-qa' | 'claude-modernize' | 'gemma-audit' | 'gemma-context' | 'claude-context-judge' | 'claude-clara-author' | 'claude-clara-review' | 'gemini-gcp-advisor' | 'claude-gcp-synthesis' | 'context-curve-plan' | 'framing-arm' | 'framing-judge' | 'claude-consolidation-design' | 'gemma-consolidation-build' | 'claude-curve-judge';
	preferredModel: string;
	fallbackModel?: string;
	systemPrompt: string;
	userPrompt: string;
	timeoutSeconds: number;
}

interface BridgeTaskResult {
	taskId: string;
	sessionId: string;
	status: 'completed' | 'failed';
	modelRequested: string;
	modelUsed?: string;
	fallbackUsed: boolean;
	durationMs: number;
	content?: string;
	errorCode?: string;
	errorMessage?: string;
}

interface ReviewTraceRecord {
	type: 'trace';
	sequence: number;
	stage: string;
	evidence: string;
	decision: string;
}

interface ReviewRecord {
	type: 'review';
	summary: string;
	strengths: string[];
	improvements: string[];
	unsupportedAssumptions: string[];
	suggestedPrompt: string;
	evidenceQuotes: string[];
}

const tokenKey = 'pocTraining.bridgeToken';
let controller: AbortController | undefined;
let statusBar: vscode.StatusBarItem;
let output: vscode.OutputChannel;

export function activate(context: vscode.ExtensionContext): void {
	output = vscode.window.createOutputChannel('POC Interactive Training');
	statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
	statusBar.command = 'pocTraining.connect';
	setStatus('disconnected');
	statusBar.show();

	context.subscriptions.push(
		output,
		statusBar,
		vscode.commands.registerCommand('pocTraining.connect', () => connect(context)),
		vscode.commands.registerCommand('pocTraining.disconnect', () => disconnect(context)),
		vscode.commands.registerCommand('pocTraining.openLesson', () => openLesson())
	);
}

async function connect(context: vscode.ExtensionContext): Promise<void> {
	const serverUrl = getServerUrl();
	const existing = await context.secrets.get(tokenKey);
	const token = await vscode.window.showInputBox({
		title: 'Connect to the local OKF training POC',
		prompt: `Paste the session token shown at ${serverUrl}`,
		value: existing,
		password: true,
		ignoreFocusOut: true,
		validateInput: value => value.trim().length < 20 ? 'Enter the complete session token.' : undefined
	});
	if (!token) {
		return;
	}

	disconnectLoop();
	controller = new AbortController();
	try {
		await postJson(`${serverUrl}/api/bridge/heartbeat`, token, undefined, controller.signal);
		await context.secrets.store(tokenKey, token);
		setStatus('connected');
		log(`Connected to ${serverUrl}. Model tasks run automatically and their status is logged here.`);
		output.show(true);
		void pollLoop(serverUrl, token, controller.signal);
	} catch (error) {
		disconnectLoop();
		setStatus('error');
		log(`Could not connect to ${serverUrl}: ${errorMessage(error)}`);
		void vscode.window.showErrorMessage(`Could not connect to ${serverUrl}: ${errorMessage(error)}`);
	}
}

async function disconnect(context: vscode.ExtensionContext): Promise<void> {
	disconnectLoop();
	await context.secrets.delete(tokenKey);
	setStatus('disconnected');
	log('POC training bridge disconnected.');
}

async function openLesson(): Promise<void> {
	await vscode.env.openExternal(vscode.Uri.parse(getServerUrl()));
}

async function pollLoop(serverUrl: string, token: string, signal: AbortSignal): Promise<void> {
	const interval = vscode.workspace.getConfiguration('pocTraining').get<number>('pollIntervalMs', 1500);
	while (!signal.aborted) {
		try {
			await postJson(`${serverUrl}/api/bridge/heartbeat`, token, undefined, signal);
			const response = await fetch(`${serverUrl}/api/bridge/tasks/next`, {
				headers: { 'X-Training-Token': token },
				signal
			});
			if (response.status === 401) {
				throw new Error('The session token was rejected. Reconnect with the current token.');
			}
			if (response.ok && response.status !== 204) {
				const task = await response.json() as BridgeTask;
				await runTask(serverUrl, token, task, signal);
			}
			setStatus('connected');
		} catch (error) {
			if (signal.aborted) {
				return;
			}
			setStatus('error');
			console.error('[POC Training]', error);
		}

		await delay(interval, signal);
	}
}

async function runTask(serverUrl: string, token: string, task: BridgeTask, signal: AbortSignal): Promise<void> {
	const label = taskLabel(task);

	// Competency 12: exact-action approval. Batch scenes make per-call prompts impractical, so this is a
	// setting rather than a removal — turning it off is a recorded choice, not an accident.
	if (vscode.workspace.getConfiguration('pocTraining').get<boolean>('requireApproval', false)) {
		log(`${label}: awaiting approval…`);
		const choice = await vscode.window.showInformationMessage(
			`Approve this exact action?\n\nTask: ${label}\nModel requested: ${task.preferredModel}\nPrompt: ${task.userPrompt.length} chars`,
			{ modal: true },
			'Approve', 'Deny');
		if (choice !== 'Approve') {
			log(`${label}: DENIED by operator. Recorded as a refusal, not a failure.`);
			await submitResult(serverUrl, token, {
				taskId: task.taskId,
				sessionId: task.sessionId,
				status: 'failed',
				modelRequested: task.preferredModel,
				fallbackUsed: false,
				durationMs: 0,
				errorCode: 'operator_denied',
				errorMessage: 'The operator denied this action at the approval gate.'
			}, signal);
			return;
		}
	}

	log(`${label}: task received — connecting to a language model…`);
	setStatus(task.kind === 'coach-narration' ? 'coaching' : 'reviewing');
	const result = await executeTask(task, serverUrl, token, signal);
	if (result.status === 'completed') {
		log(`${label}: connected to ${result.modelUsed}${result.fallbackUsed ? ' (substitute model)' : ''} and completed in ${result.durationMs} ms.`);
	} else {
		log(`${label}: failed — ${result.errorMessage ?? 'unknown error'}`);
	}
	await submitResult(serverUrl, token, result, signal);
}

function taskLabel(task: BridgeTask): string {
	switch (task.kind) {
		case 'claude-review':
			return 'Claude review';
		case 'model-answer':
			return `${task.preferredModel} answer`;
		case 'model-judge':
			return `Judge (${task.preferredModel})`;
		case 'gemma-story':
			return `${task.preferredModel} JIRA story`;
		case 'gemma-recreate':
			return `${task.preferredModel} class re-creation`;
		case 'claude-roundtrip-qa':
			return 'Claude round-trip QA';
		case 'claude-modernize':
			return 'Claude modernization';
		case 'gemma-audit':
			return `${task.preferredModel} audit`;
		case 'gemma-context':
			return `${task.preferredModel} context audit`;
		case 'claude-context-judge':
			return 'Claude context grading';
		case 'claude-clara-author':
			return 'Claude CLARA authoring';
		case 'claude-clara-review':
			return 'Claude CLARA review';
		case 'gemini-gcp-advisor':
			return `${task.preferredModel} GCP advisory`;
		case 'claude-gcp-synthesis':
			return 'Claude grounded synthesis';
		case 'context-curve-plan':
			return `${task.preferredModel} plan (context tier)`;
		case 'claude-curve-judge':
			return 'Claude context comparison';
		case 'framing-arm':
			return 'Framing arm';
		case 'framing-judge':
			return 'Claude framing review';
		case 'claude-consolidation-design':
			return 'Claude consolidation design';
		case 'gemma-consolidation-build':
			return `${task.preferredModel} implementation`;
		default:
			return `${task.preferredModel} narration`;
	}
}

async function executeTask(task: BridgeTask, serverUrl: string, token: string, signal: AbortSignal): Promise<BridgeTaskResult> {
	const started = Date.now();
	try {
		let preferred = await findModel(task.preferredModel);
		let substitute = false;
		if (!preferred && task.kind !== 'coach-narration') {
			preferred = await firstAvailableModel();
			substitute = preferred !== undefined;
		}
		if (!preferred) {
			throw new Error(`Model '${task.preferredModel}' is not available. Sign in to a VS Code language model (for example Copilot) and try again.`);
		}
		const content = task.kind === 'claude-review'
			? await requestReviewModel(preferred, task, serverUrl, token, signal)
			: await requestModel(preferred, task);
		if (!content.trim()) {
			throw new Error('The model returned no text.');
		}
		return completed(task, preferred.name, substitute, started, content);
	} catch (preferredError) {
		if (task.kind !== 'coach-narration') {
			return failed(task, started, 'model_request_failed', preferredError);
		}

		try {
			const fallback = (task.fallbackModel ? await findModel(task.fallbackModel) : undefined) ?? await firstAvailableModel();
			if (!fallback) {
				throw new Error('No language model is available in VS Code for narration.');
			}
			const content = await requestModel(fallback, task);
			if (!content.trim()) {
				throw new Error('The fallback model returned no text.');
			}
			return completed(task, fallback.name, true, started, content);
		} catch (fallbackError) {
			return failed(task, started, 'coach_and_fallback_failed', fallbackError);
		}
	}
}

async function requestReviewModel(model: vscode.LanguageModelChat, task: BridgeTask, serverUrl: string, token: string, signal: AbortSignal): Promise<string> {
	const source = new vscode.CancellationTokenSource();
	const timer = setTimeout(() => source.cancel(), task.timeoutSeconds * 1000);
	let buffer = '';
	let raw = '';
	let review: ReviewRecord | undefined;
	try {
		const prompt = ['<instructions>', task.systemPrompt, '</instructions>', '<task>', task.userPrompt, '</task>'].join('\n');
		const response = await model.sendRequest([vscode.LanguageModelChatMessage.User(prompt)], {}, source.token);
		for await (const fragment of response.text) {
			raw += fragment;
			buffer += fragment;
			let newline = buffer.indexOf('\n');
			while (newline >= 0) {
				const line = buffer.slice(0, newline).trim();
				buffer = buffer.slice(newline + 1);
				review = await processReviewLine(line, review, task, serverUrl, token, signal);
				newline = buffer.indexOf('\n');
			}
		}

		review = await processReviewLine(buffer.trim(), review, task, serverUrl, token, signal);
		if (!review) {
			review = await recoverFromBuffer(raw, task, serverUrl, token, signal);
		}
		if (!review) {
			throw new Error('The reviewer did not return a usable review record.');
		}

		const { type: _type, ...payload } = review;
		return JSON.stringify(payload);
	} finally {
		clearTimeout(timer);
		source.dispose();
	}
}

async function processReviewLine(
	line: string,
	currentReview: ReviewRecord | undefined,
	task: BridgeTask,
	serverUrl: string,
	token: string,
	signal: AbortSignal
): Promise<ReviewRecord | undefined> {
	if (!line || line.startsWith('```')) {
		return currentReview;
	}

	let record: unknown;
	try {
		record = JSON.parse(line);
	} catch {
		return currentReview;
	}

	if (isTraceRecord(record)) {
		await forwardTrace(record, task, serverUrl, token, signal);
		return currentReview;
	}

	return isReviewRecord(record) ? record : currentReview;
}

async function recoverFromBuffer(raw: string, task: BridgeTask, serverUrl: string, token: string, signal: AbortSignal): Promise<ReviewRecord | undefined> {
	let review: ReviewRecord | undefined;
	for (const candidate of extractJsonObjects(raw)) {
		let record: unknown;
		try {
			record = JSON.parse(candidate);
		} catch {
			continue;
		}
		if (isTraceRecord(record)) {
			await forwardTrace(record, task, serverUrl, token, signal);
		} else if (isReviewRecord(record)) {
			review = record;
		}
	}
	return review;
}

async function forwardTrace(record: ReviewTraceRecord, task: BridgeTask, serverUrl: string, token: string, signal: AbortSignal): Promise<void> {
	try {
		await postJson(`${serverUrl}/api/bridge/events`, token, {
			taskId: task.taskId,
			sessionId: task.sessionId,
			sequence: record.sequence,
			stage: record.stage,
			evidence: record.evidence,
			decision: record.decision,
			timestamp: new Date().toISOString()
		}, signal);
	} catch (error) {
		console.error('[POC Training] Could not forward a review trace event.', error);
	}
}

function isTraceRecord(value: unknown): value is ReviewTraceRecord {
	if (!isRecord(value)) {
		return false;
	}
	return value.type === 'trace' && Number.isInteger(value.sequence) &&
		typeof value.stage === 'string' && typeof value.evidence === 'string' && typeof value.decision === 'string';
}

function isReviewRecord(value: unknown): value is ReviewRecord {
	if (!isRecord(value)) {
		return false;
	}
	return value.type === 'review' && typeof value.summary === 'string' &&
		Array.isArray(value.strengths) && Array.isArray(value.improvements) &&
		Array.isArray(value.unsupportedAssumptions) && typeof value.suggestedPrompt === 'string' &&
		Array.isArray(value.evidenceQuotes);
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === 'object' && value !== null;
}

function extractJsonObjects(text: string): string[] {
	const objects: string[] = [];
	let depth = 0;
	let start = -1;
	let inString = false;
	let escaped = false;
	for (let index = 0; index < text.length; index++) {
		const character = text[index];
		if (inString) {
			if (escaped) {
				escaped = false;
			} else if (character === '\\') {
				escaped = true;
			} else if (character === '"') {
				inString = false;
			}
			continue;
		}
		if (character === '"') {
			inString = true;
		} else if (character === '{') {
			if (depth === 0) {
				start = index;
			}
			depth++;
		} else if (character === '}' && depth > 0) {
			depth--;
			if (depth === 0 && start >= 0) {
				objects.push(text.slice(start, index + 1));
				start = -1;
			}
		}
	}
	return objects;
}

async function findModel(requested: string): Promise<vscode.LanguageModelChat | undefined> {
	const models = await vscode.lm.selectChatModels();
	if (models.length === 0) {
		return undefined;
	}
	const expected = normalize(requested);
	const exact = models.find(model => normalize(model.name) === expected)
		?? models.find(model => [model.id, model.family, model.version].some(value => normalize(value) === expected));
	if (exact) {
		return exact;
	}
	const keywords = familyKeywords(requested);
	if (keywords.length > 0) {
		const byFamily = models.find(model =>
			[model.name, model.family, model.vendor, model.id]
				.some(value => keywords.some(keyword => normalize(value).includes(keyword))));
		if (byFamily) {
			return byFamily;
		}
	}
	return models.find(model => normalize(model.name).includes(expected) || expected.includes(normalize(model.name)));
}

async function firstAvailableModel(): Promise<vscode.LanguageModelChat | undefined> {
	const models = await vscode.lm.selectChatModels();
	return models[0];
}

function familyKeywords(requested: string): string[] {
	const value = normalize(requested);
	const groups: string[][] = [
		['claude', 'sonnet', 'opus', 'haiku', 'anthropic'],
		['gpt', 'openai', 'sol', 'o1', 'o3', 'o4'],
		['gemini', 'gemma', 'google'],
		['llama', 'meta'],
		['mistral', 'mixtral'],
		['phi']
	];
	return groups.find(group => group.some(keyword => value.includes(keyword))) ?? [];
}

async function requestModel(model: vscode.LanguageModelChat, task: BridgeTask): Promise<string> {
	const source = new vscode.CancellationTokenSource();
	const timer = setTimeout(() => source.cancel(), task.timeoutSeconds * 1000);
	try {
		const prompt = ['<instructions>', task.systemPrompt, '</instructions>', '<task>', task.userPrompt, '</task>'].join('\n');
		const response = await model.sendRequest([vscode.LanguageModelChatMessage.User(prompt)], {}, source.token);
		let content = '';
		for await (const fragment of response.text) {
			content += fragment;
		}
		return content;
	} finally {
		clearTimeout(timer);
		source.dispose();
	}
}

async function submitResult(serverUrl: string, token: string, result: BridgeTaskResult, signal: AbortSignal): Promise<void> {
	await postJson(`${serverUrl}/api/bridge/results`, token, result, signal);
}

async function postJson(url: string, token: string, body: unknown, signal: AbortSignal): Promise<void> {
	const response = await fetch(url, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json', 'X-Training-Token': token },
		body: body === undefined ? undefined : JSON.stringify(body),
		signal
	});
	if (!response.ok) {
		throw new Error(`${response.status} ${response.statusText}`);
	}
}

function completed(task: BridgeTask, modelUsed: string, fallbackUsed: boolean, started: number, content: string): BridgeTaskResult {
	return { taskId: task.taskId, sessionId: task.sessionId, status: 'completed', modelRequested: task.preferredModel, modelUsed, fallbackUsed, durationMs: Date.now() - started, content };
}

function failed(task: BridgeTask, started: number, code: string, error: unknown): BridgeTaskResult {
	return { taskId: task.taskId, sessionId: task.sessionId, status: 'failed', modelRequested: task.preferredModel, fallbackUsed: false, durationMs: Date.now() - started, errorCode: code, errorMessage: errorMessage(error) };
}

function getServerUrl(): string {
	return vscode.workspace.getConfiguration('pocTraining').get<string>('serverUrl', 'http://127.0.0.1:5000').replace(/\/$/, '');
}

function setStatus(status: 'disconnected' | 'connected' | 'coaching' | 'reviewing' | 'error'): void {
	const values = {
		disconnected: ['$(debug-disconnect)', 'POC Training: disconnected'],
		connected: ['$(plug)', 'POC Training: connected'],
		coaching: ['$(loading~spin)', 'POC Training: Gemma coaching'],
		reviewing: ['$(loading~spin)', 'POC Training: Claude reviewing'],
		error: ['$(warning)', 'POC Training: connection issue']
	} as const;
	statusBar.text = values[status][0];
	statusBar.tooltip = values[status][1];
}

function disconnectLoop(): void {
	controller?.abort();
	controller = undefined;
}

function log(message: string): void {
	output.appendLine(`[${new Date().toLocaleTimeString()}] ${message}`);
}

function normalize(value: string): string {
	return value.toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : String(error);
}

async function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
	await new Promise<void>(resolve => {
		const timer = setTimeout(resolve, milliseconds);
		signal.addEventListener('abort', () => { clearTimeout(timer); resolve(); }, { once: true });
	});
}

export function deactivate(): void {
	disconnectLoop();
}
