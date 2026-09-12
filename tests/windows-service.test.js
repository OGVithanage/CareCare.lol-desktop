import { test } from 'node:test'
import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import { applyWindowsAllowlist, PIPE_PATH } from '../src/windows-service.js'
import { migrateLegacy } from '../src/allowlist.js'
import { normalizeWebsite } from '../src/website.js'

function connection(reply) {
  const socket = new EventEmitter()
  socket.setEncoding = () => {}
  socket.destroy = () => { socket.destroyed = true }
  socket.write = (data) => reply(socket, JSON.parse(data))
  return {
    socket,
    connect(path) {
      assert.equal(path, PIPE_PATH)
      queueMicrotask(() => socket.emit('connect'))
      return socket
    }
  }
}

test('normalizes origins, Unicode and trailing-dot domains', () => {
  assert.equal(normalizeWebsite(' Example.COM/path?q=1 '), 'https://example.com')
  assert.equal(normalizeWebsite('https://example.com.:8443/a'), 'https://example.com')
  assert.equal(normalizeWebsite('bücher.de'), 'https://xn--bcher-kva.de')
})

test('all main-site variants share one rule without widening other subdomains', () => {
  for (const scheme of ['', 'http://', 'https://']) {
    for (const host of ['google.com', 'www.google.com', 'WWW.Google.COM.']) {
      assert.equal(normalizeWebsite(`${scheme}${host}`), 'https://google.com')
    }
  }
  assert.equal(normalizeWebsite('https://mail.google.com'), 'https://mail.google.com')
  assert.equal(normalizeWebsite('www.mail.google.com'), 'https://mail.google.com')
})

test('rejects unsupported or misleading domain input', () => {
  for (const value of ['', 'file:///tmp/a', 'https://user:password@example.com', '127.0.0.1', '[::1]', 'localhost', '*.example.com', '-bad.example', 'a_b.example', 'a'.repeat(64) + '.com']) {
    assert.throws(() => normalizeWebsite(value), value)
  }
})

test('does not claim enforcement on other platforms', async () => {
  const result = await applyWindowsAllowlist(migrateLegacy([]), { platform: 'darwin', connect: () => assert.fail() })
  assert.equal(result.applied, false)
})

test('sends full versioned list and handles fragmented replies', async () => {
  const mock = connection((socket, request) => {
    assert.deepEqual(request, { version: 2, websites: [{ domain: 'example.com', allowSubdomains: false }] })
    socket.emit('data', '{"version":2,"applied":true,')
    socket.emit('data', '"message":"Applied"}\n')
  })
  assert.deepEqual(await applyWindowsAllowlist(migrateLegacy(['https://example.com']), { platform: 'win32', connect: mock.connect }), { applied: true, message: 'Applied' })
  assert.equal(mock.socket.destroyed, true)
})

for (const [name, reply] of Object.entries({
  invalid: (socket) => socket.emit('data', 'invalid\n'),
  oversized: (socket) => socket.emit('data', 'x'.repeat(65537)),
  truncated: (socket) => socket.emit('end'),
  unavailable: (socket) => socket.emit('error', new Error('ENOENT')),
  timeout: () => {}
})) {
  test(`reports ${name} without claiming enforcement`, async () => {
    const mock = connection(reply)
    const result = await applyWindowsAllowlist(migrateLegacy([]), { platform: 'win32', connect: mock.connect, timeout: 10 })
    assert.equal(result.applied, false)
    assert.equal(mock.socket.destroyed, true)
  })
}

test('preserves service rejection', async () => {
  const mock = connection((socket) => socket.emit('data', '{"version":2,"applied":false,"message":"WFP transaction failed"}\n'))
  assert.deepEqual(await applyWindowsAllowlist(migrateLegacy([]), { platform: 'win32', connect: mock.connect }), { applied: false, message: 'WFP transaction failed' })
})

test('rejects incompatible responses without a legacy retry', async () => {
  let requests = 0
  const mock = connection((socket, request) => {
    requests++
    assert.equal(request.websites[0].allowSubdomains, true)
    socket.emit('data', '{"version":1,"applied":true,"message":"Applied"}\n')
  })
  const result = await applyWindowsAllowlist({ version: 2, websites: [{ domain: 'example.com', allowSubdomains: true }] }, { platform: 'win32', connect: mock.connect })
  assert.equal(result.applied, false)
  assert.match(result.message, /upgrade required/i)
  assert.equal(requests, 1)
})
