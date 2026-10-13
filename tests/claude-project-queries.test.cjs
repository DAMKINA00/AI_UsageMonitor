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
const threadPage = (project, id) => `/v1/code/channels/${project}/threads/${id}?limit=200`;
const session = id => `/v1/code/sessions/${id}`;
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
    [threadPage('one', 'working')]: { data: [] },
    [threadPage('one', 'waiting')]: { data: [] },
  });
  const result = await query(f.fetch);
  assert.equal(result.status, 200);
  assert.equal(result.chats.length, 4);
  assert.equal(result.chats[0].uuid, 'regular');
  const work = result.chats.find(x => x.uuid === 'working');
  assert.deepEqual(work, {
    source_kind: 'project_thread', uuid: 'working', name: 'Task working', project: 'Project One',
    project_uuid: 'one', thread_status: 'working', pc_status: null, pc_updated_at: null,
    updated_at: '2026-10-12T09:00:00Z', workspace_session_id: 'session_working',
  });
  assert.equal(result.chats.find(x => x.uuid === 'waiting').thread_status, 'needs_input');
  assert.ok(!JSON.stringify(result).includes('PRIVATE_BODY'));
  assert.ok(!f.calls.some(url => url.includes('/archived/')));
});

const activeSession = (id, overrides = {}) => ({
  id, environment_kind: 'bridge', status: 'active', connection_status: 'connected',
  worker_status: 'running', last_event_at: '2026-10-12T09:30:00Z', ...overrides,
});

test('idle cloud thread gets the active bridge worker state and newer activity', async () => {
  const f = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [thread('idle-pc', 'idle')] },
    [threadPage('one', 'idle-pc')]: { data: [{
      author_session_id: 'cse_bridge',
      bound_sessions: [{ id: 'cse_cloud', title: 'Cloud coordinator' }],
      body: 'PRIVATE_THREAD_BODY_MUST_NOT_ESCAPE',
    }] },
    [session('session_bridge')]: { body: activeSession('session_bridge') },
    [session('session_cloud')]: { body: activeSession('session_cloud', { environment_kind: 'cloud' }) },
  });
  const result = await query(f.fetch);
  const row = result.chats.find(x => x.uuid === 'idle-pc');
  assert.equal(row.pc_status, 'running');
  assert.equal(row.pc_updated_at, '2026-10-12T09:30:00Z');
  assert.ok(!JSON.stringify(result).includes('PRIVATE_THREAD_BODY'));
});

test('requires_action wins, while disconnected, cloud, and archived sessions stay excluded', async () => {
  const f = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [
      thread('priority', 'idle'), thread('disconnected', 'idle'), thread('cloud-only', 'idle'), thread('archived', 'archived'),
    ] },
    [threadPage('one', 'priority')]: { data: [
      { author_session_id: 'cse_running' }, { bound_sessions: [{ id: 'cse_action' }] },
    ] },
    [threadPage('one', 'disconnected')]: { data: [{ author_session_id: 'cse_disconnected' }] },
    [threadPage('one', 'cloud-only')]: { data: [{ author_session_id: 'cse_cloudonly' }] },
    [session('session_running')]: { body: activeSession('session_running') },
    [session('session_action')]: { body: activeSession('session_action', { worker_status: 'requires_action' }) },
    [session('session_disconnected')]: { body: activeSession('session_disconnected', { connection_status: 'disconnected' }) },
    [session('session_cloudonly')]: { body: activeSession('session_cloudonly', { environment_kind: 'cloud' }) },
  });
  const result = await query(f.fetch);
  assert.equal(result.chats.find(x => x.uuid === 'priority').pc_status, 'requires_action');
  assert.equal(result.chats.find(x => x.uuid === 'disconnected').pc_status, null);
  assert.equal(result.chats.find(x => x.uuid === 'cloud-only').pc_status, null);
  assert.equal(result.chats.find(x => x.uuid === 'archived').pc_status, null);
  assert.ok(f.calls.includes(session('session_disconnected')));
  assert.ok(f.calls.includes(session('session_cloudonly')));
  assert.ok(!f.calls.some(url => url === threadPage('one', 'archived')));
});

test('thread and session failures are fatal, but missing sessions are ignored', async () => {
  const threadFailure = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [thread('broken-thread', 'idle')] },
    [threadPage('one', 'broken-thread')]: { status: 503, body: {} },
  });
  await assert.rejects(query(threadFailure.fetch), /프로젝트 작업 조회 실패 \(HTTP 503\)/);

  const sessionFailure = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [thread('broken-session', 'idle')] },
    [threadPage('one', 'broken-session')]: { data: [{ author_session_id: 'cse_broken' }] },
    [session('session_broken')]: { status: 503, body: {} },
  });
  await assert.rejects(query(sessionFailure.fetch), /PC 작업 상태 조회 실패 \(HTTP 503\)/);

  const missingSession = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [thread('missing-session', 'idle')] },
    [threadPage('one', 'missing-session')]: { data: [{ author_session_id: 'cse_missing' }] },
    [session('session_missing')]: { status: 404, body: {} },
  });
  const result = await query(missingSession.fetch);
  assert.equal(result.chats.find(x => x.uuid === 'missing-session').pc_status, null);
});

test('thread message pagination reaches the end and reads a duplicate session once', async () => {
  const f = fixture({
    [channels]: { data: [{ id: 'one', name: 'Project One' }] },
    [timeline('one')]: { data: [thread('paged', 'idle')] },
    [threadPage('one', 'paged')]: { data: [{
      author_session_id: 'cse_shared', body: 'PRIVATE_PAGED_BODY_MUST_NOT_ESCAPE',
    }], next_cursor: 'page/2' },
    [threadPage('one', 'paged') + '&cursor=page%2F2']: { data: [{
      bound_sessions: [{ id: 'cse_shared' }], author_session_id: 'cse_shared',
    }] },
    [session('session_shared')]: { body: activeSession('session_shared', { response_shape: activeSession('session_shared') }) },
  });
  const result = await query(f.fetch);
  assert.equal(result.chats.find(x => x.uuid === 'paged').pc_status, 'running');
  assert.equal(f.calls.filter(url => url === session('session_shared')).length, 1);
  assert.ok(f.calls.includes(threadPage('one', 'paged') + '&cursor=page%2F2'));
  assert.ok(!JSON.stringify(result).includes('PRIVATE_PAGED_BODY'));
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
