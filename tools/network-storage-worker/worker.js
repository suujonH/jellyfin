// Cloudflare Worker that serves the objects of S3 buckets to clients the Jellyfin server gives addresses to,
// for network storage published with the "Cloudflare Worker" authentication.
//
// Address: {route}/{bucket}/{key}?exp={unix seconds}&sig={signature}
// The signature is the unpadded base64url HMAC-SHA256, keyed with PLAYBACK_SECRET, of
// "v2\n" + bucket + "\n" + key + "\n" + exp, where bucket and key are percent-decoded.
// The server writes these addresses in Jellyfin.Server.Implementations/NetworkStorage/WorkerUrlSigner.cs.
//
// Environment:
//   PLAYBACK_SECRET  the secret entered in the dashboard (Network Storage > External address)
//   S3_ENDPOINT      the S3 endpoint, e.g. https://ACCOUNT_ID.r2.cloudflarestorage.com
//   S3_REGION        the signing region, e.g. auto for R2 (default us-east-1)
//   S3_ACCESS_KEY    an access key that can read the buckets
//   S3_SECRET_KEY    its secret
//   S3_BUCKETS       the buckets served, comma-separated
//   ALLOWED_ORIGINS  origins allowed to read with CORS, comma-separated, or * (default *)
//   ROUTE_PREFIX     the path of the address entered in the dashboard, such as playback for
//                    https://media.example.com/playback (default none)

const encoder = new TextEncoder();

export default {
    async fetch(request, env) {
        const cors = corsHeaders(request, env);
        if (request.method === 'OPTIONS') {
            return new Response(null, { status: 204, headers: cors });
        }

        if (request.method !== 'GET' && request.method !== 'HEAD') {
            return new Response('Method not allowed', { status: 405, headers: cors });
        }

        const url = new URL(request.url);
        const parsed = parsePath(url.pathname, env.ROUTE_PREFIX);
        if (!parsed) {
            return new Response('Not found', { status: 404, headers: cors });
        }

        const { bucket, key } = parsed;
        const buckets = (env.S3_BUCKETS || '').split(',').map(b => b.trim()).filter(Boolean);
        if (!buckets.includes(bucket)) {
            return new Response('Not found', { status: 404, headers: cors });
        }

        const exp = url.searchParams.get('exp') || '';
        const sig = url.searchParams.get('sig') || '';
        if (!/^\d+$/.test(exp) || Number(exp) < Date.now() / 1000) {
            return new Response('Expired', { status: 403, headers: cors });
        }

        if (!timingSafeEqual(sig, await sign(env.PLAYBACK_SECRET, bucket, key, exp))) {
            return new Response('Forbidden', { status: 403, headers: cors });
        }

        const upstream = await presign(env, request.method, bucket, key, new Date());
        const headers = new Headers();
        for (const name of ['Range', 'If-None-Match', 'If-Modified-Since', 'If-Range']) {
            const value = request.headers.get(name);
            if (value) {
                headers.set(name, value);
            }
        }

        const response = await fetch(upstream, { method: request.method, headers });
        const out = new Headers(response.headers);
        for (const [name, value] of Object.entries(cors)) {
            out.set(name, value);
        }

        // The address is the same for an hour; whoever holds it may read the object, so caches may keep it until it expires.
        if (response.ok) {
            out.set('Cache-Control', 'public, max-age=' + Math.max(0, Math.floor(Number(exp) - Date.now() / 1000)));
        }

        return new Response(response.body, { status: response.status, headers: out });
    }
};

export function parsePath(pathname, routePrefix) {
    // The address entered in the dashboard may carry a path, such as https://media.example.com/playback.
    const prefix = (routePrefix || '').replace(/^\/+|\/+$/g, '');
    let path = pathname;
    if (prefix) {
        if (!path.startsWith('/' + prefix + '/')) {
            return null;
        }

        path = path.slice(prefix.length + 1);
    }

    // Only the separator before the bucket is dropped: a key may hold empty segments, as in a//b, and they are signed.
    const parts = path.replace(/^\//, '').split('/');
    if (parts.length < 2 || parts[0].length === 0 || parts.slice(1).join('/').length === 0) {
        return null;
    }

    try {
        return { bucket: decodeURIComponent(parts[0]), key: parts.slice(1).map(decodeURIComponent).join('/') };
    } catch {
        return null;
    }
}

export async function sign(secret, bucket, key, exp) {
    const mac = await hmac(encoder.encode(secret), 'v2\n' + bucket + '\n' + key + '\n' + exp);
    return base64Url(mac);
}

function timingSafeEqual(a, b) {
    if (a.length !== b.length) {
        return false;
    }

    let diff = 0;
    for (let i = 0; i < a.length; i++) {
        diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
    }

    return diff === 0;
}

function corsHeaders(request, env) {
    const allowed = (env.ALLOWED_ORIGINS || '*').split(',').map(o => o.trim());
    const origin = request.headers.get('Origin');
    const allowOrigin = allowed.includes('*') ? '*' : (origin && allowed.includes(origin) ? origin : null);
    const headers = {
        'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS',
        'Access-Control-Allow-Headers': 'Range, If-None-Match, If-Modified-Since, If-Range',
        'Access-Control-Expose-Headers': 'Content-Length, Content-Range, Accept-Ranges, ETag'
    };
    if (allowOrigin) {
        headers['Access-Control-Allow-Origin'] = allowOrigin;
        headers.Vary = 'Origin';
    }

    return headers;
}

// A Signature Version 4 address presigned for five minutes, path-style, as S3UrlSigner builds them.
export async function presign(env, method, bucket, key, now) {
    const region = env.S3_REGION || 'us-east-1';
    const endpoint = new URL(env.S3_ENDPOINT);
    const canonicalUri = endpoint.pathname.replace(/\/+$/, '') + '/' + encode(bucket, false) + '/' + encode(key, true);
    const timestamp = now.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, '');
    const date = timestamp.slice(0, 8);
    const scope = date + '/' + region + '/s3/aws4_request';
    const query = [
        ['X-Amz-Algorithm', 'AWS4-HMAC-SHA256'],
        ['X-Amz-Credential', env.S3_ACCESS_KEY + '/' + scope],
        ['X-Amz-Date', timestamp],
        ['X-Amz-Expires', '300'],
        ['X-Amz-SignedHeaders', 'host']
    ].map(([n, v]) => encode(n, false) + '=' + encode(v, false)).join('&');

    const canonicalRequest = method + '\n' + canonicalUri + '\n' + query + '\nhost:' + endpoint.host + '\n\nhost\nUNSIGNED-PAYLOAD';
    const stringToSign = 'AWS4-HMAC-SHA256\n' + timestamp + '\n' + scope + '\n' + hex(await crypto.subtle.digest('SHA-256', encoder.encode(canonicalRequest)));

    let signingKey = await hmac(encoder.encode('AWS4' + env.S3_SECRET_KEY), date);
    signingKey = await hmac(signingKey, region);
    signingKey = await hmac(signingKey, 's3');
    signingKey = await hmac(signingKey, 'aws4_request');
    const signature = hex(await hmac(signingKey, stringToSign));

    return endpoint.protocol + '//' + endpoint.host + canonicalUri + '?' + query + '&X-Amz-Signature=' + signature;
}

async function hmac(key, data) {
    const cryptoKey = await crypto.subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    return new Uint8Array(await crypto.subtle.sign('HMAC', cryptoKey, encoder.encode(data)));
}

function encode(value, keepSlash) {
    let result = '';
    for (const b of encoder.encode(value)) {
        const c = String.fromCharCode(b);
        if (/[A-Za-z0-9\-_.~]/.test(c) || (keepSlash && c === '/')) {
            result += c;
        } else {
            result += '%' + b.toString(16).toUpperCase().padStart(2, '0');
        }
    }

    return result;
}

function hex(buffer) {
    return [...new Uint8Array(buffer)].map(b => b.toString(16).padStart(2, '0')).join('');
}

function base64Url(bytes) {
    let binary = '';
    for (const b of bytes) {
        binary += String.fromCharCode(b);
    }

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
