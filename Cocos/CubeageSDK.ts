/**
 * CubeageSDK.ts — Cubeage Game SDK V2 for Cocos Creator
 *
 * Features:
 *   - Anonymous auth with Bearer token management
 *   - Legacy migration from old SDK (via cc.sys.localStorage)
 *   - Automatic session management
 *   - Event tracking with batch support
 *   - Offline queue with persistence
 *   - Retry with exponential backoff
 *
 * Usage:
 *   import CubeageSDK from './CubeageSDK';
 *   import config from './CubeageSDKConfig';
 *
 *   await CubeageSDK.instance.init(config);
 */

// ─── Storage Keys ────────────────────────────────────────────────────────────

const KEY_DEVICE_ID      = 'cubeage_device_id';
const KEY_ACCESS_TOKEN   = 'cubeage_access_token';
const KEY_REFRESH_TOKEN  = 'cubeage_refresh_token';
const KEY_USER_ID        = 'cubeage_user_id';
const KEY_MIGRATION_DONE = 'cubeage_migration_done';
const KEY_FIRST_LAUNCH   = 'cubeage_first_launch';
const KEY_OFFLINE_QUEUE  = 'cubeage_offline_queue';

const MAX_RETRIES   = 3;
const MAX_QUEUE_SIZE = 500;

// ─── Types ───────────────────────────────────────────────────────────────────

export interface SDKConfig {
    apiBaseUrl: string;
    gameSlug: string;
    legacyTokenKey?: string;   // cc.sys.localStorage key for the old device ID
    verboseLogging?: boolean;
}

export interface AuthResponse {
    accessToken: string;
    refreshToken: string;
    userId: string;
}

export interface SessionStartResponse {
    sessionId: string;
}

interface QueuedRequest {
    method: string;
    path: string;
    body: string | null;
    timestamp: number;
}

// ─── SDK Class ───────────────────────────────────────────────────────────────

export class CubeageSDK {
    // Singleton
    private static _instance: CubeageSDK | null = null;
    public static get instance(): CubeageSDK {
        if (!CubeageSDK._instance) {
            CubeageSDK._instance = new CubeageSDK();
        }
        return CubeageSDK._instance;
    }

    // State
    private _apiBaseUrl     = '';
    private _gameSlug       = '';
    private _legacyTokenKey = '';
    private _verbose        = false;

    private _deviceId       = '';
    private _accessToken    = '';
    private _refreshToken   = '';
    private _userId         = '';
    private _initialized    = false;
    private _sessionId: string | null = null;
    private _sessionActive  = false;

    private _offlineQueue: QueuedRequest[] = [];
    private _isFlushing = false;

    private constructor() {}

    // ─── Public API — Init ────────────────────────────────────────────────────

    /**
     * Initialize the SDK. Call once on game start (await it).
     * Runs legacy migration on first launch, then anonymous auth if needed.
     */
    public async init(config: SDKConfig): Promise<void> {
        if (this._initialized) return;

        this._apiBaseUrl     = config.apiBaseUrl.replace(/\/$/, '');
        this._gameSlug       = config.gameSlug;
        this._legacyTokenKey = config.legacyTokenKey || '';
        this._verbose        = config.verboseLogging ?? false;

        this._deviceId      = this._resolveDeviceId();
        this._accessToken   = cc.sys.localStorage.getItem(KEY_ACCESS_TOKEN)  || '';
        this._refreshToken  = cc.sys.localStorage.getItem(KEY_REFRESH_TOKEN) || '';
        this._userId        = cc.sys.localStorage.getItem(KEY_USER_ID)       || '';

        this._loadOfflineQueue();

        if (!this._accessToken) {
            // Step 1: Try legacy migration (one-time only)
            if (!cc.sys.localStorage.getItem(KEY_MIGRATION_DONE)) {
                const legacyToken = this._getLegacyToken();
                if (legacyToken) {
                    this._log(`Found legacy token — attempting migration`);
                    await this._doMigrate(legacyToken);
                }
                // Mark migration as attempted regardless of outcome
                cc.sys.localStorage.setItem(KEY_MIGRATION_DONE, '1');
            }

            // Step 2: Fallback to anonymous auth
            if (!this._accessToken) {
                await this._doAnonymousAuth();
            }

            if (!this._accessToken) {
                throw new Error('[CubeageSDK] Authentication failed — cannot init');
            }
        }

        this._initialized = true;
        this._log(`Init complete. userId=${this._userId}`);

        // Start initial session
        this._startSession().catch(e => this._logWarn(`Session start error: ${e}`));

        // Track attribution on first launch
        if (!cc.sys.localStorage.getItem(KEY_FIRST_LAUNCH)) {
            cc.sys.localStorage.setItem(KEY_FIRST_LAUNCH, '1');
            this._trackAttribution().catch(() => {});
        }

        // Flush any queued events
        this._flushQueue();
    }

    // ─── Public API — Events ──────────────────────────────────────────────────

    /** Track a custom game event. */
    public trackEvent(eventName: string, properties: Record<string, unknown> = {}): void {
        if (!this._initialized) { this._logWarn('Not initialized'); return; }
        const body = JSON.stringify({
            gameSlug: this._gameSlug,
            events: [{ name: eventName, properties }],
        });
        this._enqueueOrSend('POST', '/api/v1/sdk/event/batch', body);
    }

    // ─── Public API — Accessors ───────────────────────────────────────────────

    public get userId(): string  { return this._userId; }
    public get sessionId(): string | null { return this._sessionId; }
    public get isInitialized(): boolean { return this._initialized; }

    // ─── Device ID ────────────────────────────────────────────────────────────

    private _resolveDeviceId(): string {
        const stored = cc.sys.localStorage.getItem(KEY_DEVICE_ID);
        if (stored) return stored;

        const id = this._uuid();
        cc.sys.localStorage.setItem(KEY_DEVICE_ID, id);
        return id;
    }

    // ─── Legacy Migration ─────────────────────────────────────────────────────

    private _getLegacyToken(): string {
        if (!this._legacyTokenKey) return '';
        const token = cc.sys.localStorage.getItem(this._legacyTokenKey);
        if (token) {
            this._log(`Legacy token found via localStorage[${this._legacyTokenKey}]`);
        }
        return token || '';
    }

    private async _doMigrate(legacyToken: string): Promise<void> {
        try {
            const resp = await this._rawPost('/api/v1/sdk/migrate', {
                gameSlug: this._gameSlug,
                token: legacyToken,
            });
            if (resp && resp.accessToken) {
                this._storeTokens(resp);
                this._log(`Migration success — userId=${this._userId}`);
            }
        } catch (e) {
            this._logWarn(`Legacy migration failed (non-fatal): ${e}`);
        }
    }

    // ─── Anonymous Auth ───────────────────────────────────────────────────────

    private async _doAnonymousAuth(): Promise<void> {
        try {
            const resp = await this._rawPost('/api/v1/sdk/auth/anonymous', {
                deviceId: this._deviceId,
                gameSlug: this._gameSlug,
            });
            if (resp && resp.accessToken) {
                this._storeTokens(resp);
                this._log(`Anonymous auth OK — userId=${this._userId}`);
            }
        } catch (e) {
            this._logWarn(`Anonymous auth failed: ${e}`);
        }
    }

    // ─── Token Management ─────────────────────────────────────────────────────

    private _storeTokens(resp: AuthResponse): void {
        this._accessToken  = resp.accessToken  || '';
        this._refreshToken = resp.refreshToken || '';
        this._userId       = resp.userId       || '';
        cc.sys.localStorage.setItem(KEY_ACCESS_TOKEN,  this._accessToken);
        cc.sys.localStorage.setItem(KEY_REFRESH_TOKEN, this._refreshToken);
        cc.sys.localStorage.setItem(KEY_USER_ID,       this._userId);
    }

    private async _refreshToken_(): Promise<void> {
        if (!this._refreshToken) {
            await this._doAnonymousAuth();
            return;
        }
        try {
            const resp = await this._rawPost('/api/v1/sdk/auth/refresh', {
                refreshToken: this._refreshToken,
            });
            if (resp && resp.accessToken) {
                this._storeTokens(resp);
            } else {
                await this._doAnonymousAuth();
            }
        } catch {
            this._accessToken  = '';
            this._refreshToken = '';
            await this._doAnonymousAuth();
        }
    }

    // ─── Session Management ───────────────────────────────────────────────────

    private async _startSession(): Promise<void> {
        if (this._sessionActive) return;
        try {
            const resp = await this._authenticatedRequest<SessionStartResponse>(
                'POST', '/api/v1/sdk/session/start',
                JSON.stringify({
                    gameSlug:    this._gameSlug,
                    platform:    this._getPlatform(),
                    deviceModel: this._getDeviceModel(),
                    locale:      this._getLocale(),
                }),
            );
            if (resp?.sessionId) {
                this._sessionId    = resp.sessionId;
                this._sessionActive = true;
                this._log(`Session started: ${this._sessionId}`);
            }
        } catch (e) {
            this._logWarn(`Start session error: ${e}`);
        }
    }

    private async _endSession(): Promise<void> {
        if (!this._sessionActive || !this._sessionId) return;
        const sid = this._sessionId;
        this._sessionActive = false;
        this._sessionId     = null;
        try {
            await this._authenticatedRequest('POST', '/api/v1/sdk/session/end',
                JSON.stringify({ sessionId: sid }));
        } catch { /* non-fatal */ }
    }

    // ─── Attribution ──────────────────────────────────────────────────────────

    private async _trackAttribution(): Promise<void> {
        await this._authenticatedRequest('POST', '/api/v1/sdk/attribution/install',
            JSON.stringify({
                gameSlug:  this._gameSlug,
                platform:  this._getPlatform(),
                deviceId:  this._deviceId,
            }));
    }

    // ─── HTTP Helpers ─────────────────────────────────────────────────────────

    /** Raw unauthenticated POST (used for auth/migrate). */
    private async _rawPost<T = AuthResponse>(path: string, body: unknown): Promise<T> {
        const url = `${this._apiBaseUrl}${path}`;
        this._log(`POST ${url}`);
        const response = await fetch(url, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(body),
        });
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        return response.json() as Promise<T>;
    }

    /** Authenticated request with automatic token refresh and retry. */
    private async _authenticatedRequest<T = unknown>(
        method: string,
        path: string,
        body: string | null = null,
        retryCount = 0,
    ): Promise<T | null> {
        const url = `${this._apiBaseUrl}${path}`;
        this._log(`${method} ${url}`);

        const headers: Record<string, string> = {
            'Authorization': `Bearer ${this._accessToken}`,
        };
        if (body !== null) {
            headers['Content-Type'] = 'application/json';
        }

        try {
            const response = await fetch(url, { method, headers, body: body ?? undefined });

            if (response.ok) {
                const text = await response.text();
                if (!text) return null;
                return JSON.parse(text) as T;
            }

            if (response.status === 401 && retryCount === 0) {
                await this._refreshToken_();
                return this._authenticatedRequest<T>(method, path, body, 1);
            }

            const isRetryable = response.status === 429 || response.status >= 500;
            if (isRetryable && retryCount < MAX_RETRIES) {
                const delay = 1000 * Math.pow(2, retryCount);
                await this._sleep(delay);
                return this._authenticatedRequest<T>(method, path, body, retryCount + 1);
            }

            this._logWarn(`${method} ${path} failed: HTTP ${response.status}`);
            return null;
        } catch (e) {
            if (retryCount < MAX_RETRIES) {
                const delay = 1000 * Math.pow(2, retryCount);
                await this._sleep(delay);
                return this._authenticatedRequest<T>(method, path, body, retryCount + 1);
            }
            this._logWarn(`${method} ${path} error: ${e}`);
            return null;
        }
    }

    // ─── Offline Queue ────────────────────────────────────────────────────────

    private _enqueueOrSend(method: string, path: string, body: string): void {
        this._authenticatedRequest(method, path, body).then(result => {
            if (result === null) {
                this._enqueueOffline(method, path, body);
            }
        }).catch(() => {
            this._enqueueOffline(method, path, body);
        });
    }

    private _enqueueOffline(method: string, path: string, body: string): void {
        if (this._offlineQueue.length >= MAX_QUEUE_SIZE) {
            this._offlineQueue.shift();
        }
        this._offlineQueue.push({ method, path, body, timestamp: Date.now() });
        this._saveOfflineQueue();
    }

    private _flushQueue(): void {
        if (this._isFlushing || this._offlineQueue.length === 0) return;
        this._isFlushing = true;
        this._log(`Flushing ${this._offlineQueue.length} queued events`);

        const processNext = async (): Promise<void> => {
            if (this._offlineQueue.length === 0) {
                this._isFlushing = false;
                this._saveOfflineQueue();
                return;
            }
            const item = this._offlineQueue[0];
            const result = await this._authenticatedRequest(item.method, item.path, item.body);
            if (result !== null || item.body === null) {
                this._offlineQueue.shift();
                await this._sleep(100);
                return processNext();
            } else {
                this._isFlushing = false;
                this._saveOfflineQueue();
            }
        };

        processNext().catch(() => { this._isFlushing = false; });
    }

    private _saveOfflineQueue(): void {
        try {
            cc.sys.localStorage.setItem(KEY_OFFLINE_QUEUE, JSON.stringify(this._offlineQueue));
        } catch { /* storage full */ }
    }

    private _loadOfflineQueue(): void {
        try {
            const raw = cc.sys.localStorage.getItem(KEY_OFFLINE_QUEUE);
            if (raw) this._offlineQueue = JSON.parse(raw) || [];
        } catch { this._offlineQueue = []; }
    }

    // ─── Platform Utilities ───────────────────────────────────────────────────

    private _getPlatform(): string {
        if (cc.sys.isNative) {
            if (cc.sys.os === cc.sys.OS_IOS || cc.sys.os === cc.sys.OS_OSX) return 'ios';
            if (cc.sys.os === cc.sys.OS_ANDROID) return 'android';
        }
        if (cc.sys.isBrowser) return 'web';
        return 'unknown';
    }

    private _getDeviceModel(): string {
        try { return cc.sys.os + ' ' + cc.sys.osVersion; } catch { return 'unknown'; }
    }

    private _getLocale(): string {
        try { return cc.sys.language || 'en'; } catch { return 'en'; }
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private _uuid(): string {
        const template = 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx';
        return template.replace(/[xy]/g, (c) => {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

    private _sleep(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    private _log(msg: string): void {
        if (this._verbose) console.log(`[CubeageSDK] ${msg}`);
    }

    private _logWarn(msg: string): void {
        console.warn(`[CubeageSDK] ${msg}`);
    }
}

export default CubeageSDK;
