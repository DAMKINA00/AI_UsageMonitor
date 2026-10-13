const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const assert = require('node:assert/strict');
const { test } = require('node:test');

// Execute the shipped query code with fixture responses; no account or network access.
const source = readFileSync(join(__dirname, '../Core/ClaudeWeb.cs'), 'utf8');
const prelude = source.match(/const string Prelude = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const query = new AsyncFunction('fetch', prelude + '\nreturn await chatsOf("test-org");');
const channels = '/v1/code/channels?scope=joined&limit=200';
const timeline = id => `/v1/code/channels/${id}/timeline?limit=200&bound_only=true&thread_roots_only=true`;
const thread = (id, status) => ({
  id, thread_status: status, created_at: '2026-10-12T08:00:00Z', last_reply_at: '2026-10-12T09:00:00Z',
  body: 'PRIVATE_BODY_MUST_NOT_ESCAPE',
  bound_sessions: [{ id: 'cse_' + id, title: 'Task ' + id, status: 'active' }],
});

function fixture(routes) {
  const calls = [];
  const fetch = async (url, options) => {
    calls.push(url);
    assert.equal(options.credentials, 'include');
    assert.ok(options.signal instanceof AbortSignal);
    if (url.startsWith('/api/organizations/'))
      return { status: 200, text: async () => JSON.stringify({ data: [{ uuid: 'regular', name: 'Regular', body: 'PRIVATE_BODY_MUST_NOT_ESCAPE' }] }) };
    assert.equal(options.headers['x-organization-uuid'], 'test-org');
    assert.equal(options.headers['anthropic-client-feature'], 'ccr');
    assert.equal(options.headers['anthropic-version'], '2023-06-01');
    assert.equal(options.headers['anthropic-beta'], 'ccr-byoc-2025-07-29');
    assert.ok(Object.hasOwn(routes, url), `Unexpected URL: ${url}`);
    const response = routes[url];
    return { status: response.status ?? 200, text: async () => JSON.stringify(response.body ?? response) };
  };
  return { fetch, calls };
}

test('collects paginated projects and threads, skips archived projects and excludes bodies', async () => {
  const f = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }, { id: 'archived', archived_at: '2026-01-01' }], next_cursor: 'a/b' },
    [channels + '&cursor=a%2Fb']: { data: [{ id: 'two', name: 'Project Two' }] },
    [timeline('one')]: { data: [thread('working', 'working')], next_cursor: 'next' },
    [timeline('one') + '&cursor=next']: { data: [thread('waiting', 'needs_input')] },
    [timeline('two')]: { data: [thread('done', 'resolved'), { id: 'unbound', body: 'ignored' }] },
  });
  const result = await query(f.fetch);
  assert.equal(result.status, 200);
  assert.equal(result.chats.length, 4);
  assert.equal(result.chats[0].uuid, 'regular');
  const work = result.chats.find(x => x.uuid === 'working');
  assert.deepEqual(work, {
    source_kind: 'project_thread', uuid: 'working', name: 'Task working', project: 'Project One',
    project_uuid: 'one', thread_status: 'working', updated_at: '2026-10-12T09:00:00Z', workspace_session_id: 'session_working',
  });
  assert.equal(result.chats.find(x => x.uuid === 'waiting').thread_status, 'needs_input');
  assert.ok(!JSON.stringify(result).includes('PRIVATE_BODY'));
  assert.ok(!f.calls.some(url => url.includes('/archived/')));
});

test('empty projects preserve regular chats', async () => {
  const f = fixture({ [channels]: { data: [] } });
  const result = await query(f.fetch);
  assert.deepEqual(result.chats.map(x => x.uuid), ['regular']);
});

test('failed or malformed project responses do not report a complete snapshot', async () => {
  for (const response of [{ status: 503, body: {} }, { data: {} }]) {
    const f = fixture({ [channels]: response });
    await assert.rejects(query(f.fetch), /프로젝트 작업 조회 실패/);
  }
  const f = fixture({ [channels]: { data: [{ id: 'one' }] }, [timeline('one')]: { status: 403, body: {} } });
  await assert.rejects(query(f.fetch), /HTTP 403/);
});

test('repeating pagination cursor is rejected', async () => {
  const f = fixture({
    [channels]: { data: [], next_cursor: 'repeat' },
    [channels + '&cursor=repeat']: { data: [], next_cursor: 'repeat' },
  });
  await assert.rejects(query(f.fetch), /끝까지 읽지 못했습니다/);
  assert.equal(f.calls.filter(x => x.startsWith(channels)).length, 2);
});

test('secondary Claude accounts request usage without conversations or projects', async () => {
  const body = source.match(/const string FetchScript = [^\r\n]+"""\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
  const script = ('return (async () => {\n' + prelude + '\n' + body)
    .replaceAll('__DESKTOP__', 'false').replaceAll('__INCLUDE_CHATS__', 'false');
  const calls = [];
  const fetch = async url => {
    calls.push(url);
    const json = url === '/api/organizations' ? [{ uuid: 'org', name: 'secondary@example.test', capabilities: ['claude_pro'] }]
      : url === '/api/organizations/org/usage' ? { seven_day: { utilization: 25 } } : assert.fail('Unexpected request ' + url);
    return { status: 200, text: async () => JSON.stringify(json) };
  };
  const result = JSON.parse(await new AsyncFunction('fetch', 'document', script)(fetch, { cookie: '' }));
  assert.equal(result.state, 'ok');
  assert.equal(result.usage.seven_day.utilization, 25);
  assert.equal(result.chats, null);
  assert.equal(result.chats_error, null);
  assert.equal(calls.length, 2);
});
