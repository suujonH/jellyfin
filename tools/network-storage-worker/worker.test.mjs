// node --test tools/network-storage-worker/worker.test.mjs
import assert from 'node:assert/strict';
import test from 'node:test';
import worker, { parsePath, presign, sign } from './worker.js';

// The same vector as NetworkStorageAddressTests.WorkerUrlSigner_MatchesTheWorkerTestVector.
const address = 'https://media.example.com/media/%E7%94%B5%E5%BD%B1/A%2BB.mkv?exp=1792576800&sig=F3wrSNRrFuhGocJ1vC5yhwgZRZSia1-83EFibDTMeP0';

// The fixed vector expires; the requests use an address signed now.
const exp = String(Math.floor(Date.now() / 1000) + 3600);
const current = 'https://media.example.com/media/%E7%94%B5%E5%BD%B1/A%2BB.mkv?exp=' + exp + '&sig=' + await sign('shared-secret', 'media', '电影/A+B.mkv', exp);

const env = {
    PLAYBACK_SECRET: 'shared-secret',
    S3_ENDPOINT: 'https://s3.amazonaws.com',
    S3_REGION: 'us-east-1',
    S3_ACCESS_KEY: 'AKIAIOSFODNN7EXAMPLE',
    S3_SECRET_KEY: 'wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY',
    S3_BUCKETS: 'media'
};

test('the signature of the server is accepted', async () => {
    const url = new URL(address);
    const { bucket, key } = parsePath(url.pathname);

    assert.equal(bucket, 'media');
    assert.equal(key, '电影/A+B.mkv');
    assert.equal(await sign('shared-secret', bucket, key, url.searchParams.get('exp')), url.searchParams.get('sig'));
});

test('empty segments of a key are kept', () => {
    assert.deepEqual(parsePath('/media/Movies//Film.mkv'), { bucket: 'media', key: 'Movies//Film.mkv' });
    assert.deepEqual(parsePath('/media//Film.mkv'), { bucket: 'media', key: '/Film.mkv' });
    assert.equal(parsePath('/media/'), null);
    assert.equal(parsePath('//Film.mkv'), null);
});

test('a route prefix is removed before the bucket', () => {
    assert.deepEqual(parsePath('/playback/media/Film.mkv', 'playback'), { bucket: 'media', key: 'Film.mkv' });
    assert.deepEqual(parsePath('/playback/media/Film.mkv', '/playback/'), { bucket: 'media', key: 'Film.mkv' });
    assert.equal(parsePath('/media/Film.mkv', 'playback'), null);
});

test('a changed key is refused', async () => {
    const original = globalThis.fetch;
    globalThis.fetch = () => assert.fail('the bucket must not be read');
    try {
        const tampered = current.replace('A%2BB', 'A%2BC');
        const response = await worker.fetch(new Request(tampered), env);
        assert.equal(response.status, 403);
    } finally {
        globalThis.fetch = original;
    }
});

test('the bucket is read with the range of the client', async () => {
    const original = globalThis.fetch;
    let upstream;
    globalThis.fetch = (url, init) => {
        upstream = { url, init };
        return Promise.resolve(new Response('x', { status: 206, headers: { 'Content-Range': 'bytes 0-0/10' } }));
    };
    try {
        const response = await worker.fetch(new Request(current, { headers: { Range: 'bytes=0-0' } }), env);
        assert.equal(response.status, 206);
        assert.equal(upstream.init.headers.get('Range'), 'bytes=0-0');
        assert.ok(upstream.url.startsWith('https://s3.amazonaws.com/media/%E7%94%B5%E5%BD%B1/A%2BB.mkv?X-Amz-Algorithm=AWS4-HMAC-SHA256&'));
    } finally {
        globalThis.fetch = original;
    }
});

test('the bucket is read through a path-style presigned address', async () => {
    const url = await presign(env, 'GET', 'examplebucket', 'test.txt', new Date('2013-05-24T00:00:00Z'));
    assert.ok(url.startsWith('https://s3.amazonaws.com/examplebucket/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request&X-Amz-Date=20130524T000000Z&X-Amz-Expires=300&X-Amz-SignedHeaders=host&X-Amz-Signature='));
});
