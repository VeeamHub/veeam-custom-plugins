/**
 * Transport to the Autotask PSA plugin backend.
 *
 * All plugin endpoints are proxied by the VSPC UI at /plugins/{pluginId}/. The VSPC
 * proxy does not inject identity headers and rejects Authorization headers on plugin
 * routes, so the signed-in portal user's access token travels in a custom header;
 * the plugin service validates it against VSPC (GET /users/me as the user).
 */

const PLUGIN_ID = process.env.PLUGIN_ID as string;
const API_BASE = `/plugins/${PLUGIN_ID}`;
const TOKEN_HEADER = 'X-Vspc-Access-Token';

let cachedToken: string | undefined;

const jwtRe = /^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$/;

function tokenExpiry(token: string): number {
    try {
        const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
        return payload.exp || 0;
    } catch {
        return 0;
    }
}

/** The portal keeps its OAuth state at `VeeamVspc/General/Token` (see SDK template main.tsx). */
function readCanonicalToken(): string {
    try {
        const raw = window.localStorage.getItem('VeeamVspc/General/Token');
        if (!raw) return '';
        const parsed = JSON.parse(raw);
        const token = typeof parsed?.access_token === 'string' ? parsed.access_token.trim() : '';
        return jwtRe.test(token) && tokenExpiry(token) > Date.now() / 1000 ? token : '';
    } catch {
        return '';
    }
}

/** Fallback: scan web storage for any live JWT (prefer the shortest-lived = access token). */
function scanForToken(): string {
    const candidates: string[] = [];
    const collect = (value: unknown): void => {
        if (typeof value === 'string' && jwtRe.test(value.trim())) candidates.push(value.trim());
    };
    for (const store of [window.localStorage, window.sessionStorage]) {
        let length = 0;
        try { length = store.length; } catch { continue; }
        for (let i = 0; i < length; i++) {
            let raw: string | null = null;
            try { raw = store.getItem(store.key(i) as string); } catch { continue; }
            if (!raw || !raw.includes('eyJ')) continue;
            collect(raw);
            try {
                const stack: unknown[] = [JSON.parse(raw)];
                while (stack.length) {
                    const cur = stack.pop();
                    if (typeof cur === 'string') collect(cur);
                    else if (cur && typeof cur === 'object') Object.values(cur).forEach(v => stack.push(v));
                }
            } catch { /* not JSON */ }
        }
    }
    const now = Date.now() / 1000;
    const live = candidates
        .map(token => ({ token, exp: tokenExpiry(token) }))
        .filter(x => x.exp > now)
        .sort((a, b) => a.exp - b.exp);
    return live.length ? live[0].token : '';
}

export function refreshPluginToken(): void {
    cachedToken = readCanonicalToken() || scanForToken();
}

export class PluginApiError extends Error {
    constructor(public readonly status: number, message: string) {
        super(message);
    }
}

export async function pluginApi<T = unknown>(
    path: string,
    options: { method?: string; body?: unknown } = {},
): Promise<T> {
    if (cachedToken === undefined) refreshPluginToken();

    const init: RequestInit = {
        method: options.method || (options.body !== undefined ? 'POST' : 'GET'),
        headers: {
            'Content-Type': 'application/json',
            ...(cachedToken ? { [TOKEN_HEADER]: cachedToken } : {}),
        },
        credentials: 'same-origin',
    };
    if (options.body !== undefined) init.body = JSON.stringify(options.body);

    let res = await fetch(API_BASE + path, init);
    if (res.status === 401 || res.status === 403) {
        // Token may have rotated — rescan once and retry.
        refreshPluginToken();
        if (cachedToken) (init.headers as Record<string, string>)[TOKEN_HEADER] = cachedToken;
        res = await fetch(API_BASE + path, init);
    }

    let data: any = {};
    try { data = await res.json(); } catch { /* empty body */ }
    if (!res.ok) {
        throw new PluginApiError(res.status, data.message || data.error || `${res.status} ${res.statusText}`);
    }
    return data as T;
}
