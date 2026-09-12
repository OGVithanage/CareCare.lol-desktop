import { test } from 'node:test'
import assert from 'node:assert/strict'
import { normalizeAllowlist, migrateLegacy, loadAllowlist, fromLegacyEditor } from '../src/allowlist.js'

const payload = (websites) => ({ version: 2, websites })
const rule = (domain, allowSubdomains = false) => ({ domain, allowSubdomains })

test('normalizes rule objects and updates duplicate settings by domain', () => {
  assert.deepEqual(normalizeAllowlist(payload([
    rule('HTTP://WWW.Example.COM.:8080/path'), rule('example.com', true), rule('bücher.de')
  ])), payload([rule('example.com', true), rule('xn--bcher-kva.de')]))
})

test('requires actual booleans, valid domains, collection and version', () => {
  for (const value of [undefined, null, 0, 1, 'false', 'true'])
    assert.throws(() => normalizeAllowlist(payload([rule('example.com', value === undefined ? null : value)])))
  assert.throws(() => normalizeAllowlist(payload([{ domain: 'example.com' }])))
  for (const value of [null, [], { version: 1, websites: [] }, { version: 3, websites: [] }, payload(null), payload(['example.com']), payload([rule('127.0.0.1')])])
    assert.throws(() => normalizeAllowlist(value))
})

test('500-rule limit applies before deduplication and not to aliases', () => {
  assert.equal(normalizeAllowlist(payload(Array.from({ length: 500 }, (_, i) => rule(`site${i}.example.com`, true)))).websites.length, 500)
  assert.throws(() => normalizeAllowlist(payload(Array.from({ length: 501 }, () => rule('example.com')))))
})

test('legacy migration defaults to false and merges all main-site aliases', () => {
  assert.deepEqual(migrateLegacy(['https://google.com', 'http://WWW.Google.com./x', 'mail.google.com']), payload([rule('google.com'), rule('mail.google.com')]))
})

function fakeStore(initial) {
  const data = structuredClone(initial)
  return { data, has: key => Object.hasOwn(data, key), get: (key, fallback) => data[key] ?? fallback, set: (key, value) => { data[key] = value } }
}

test('migration is repeatable, retains legacy backup and prioritizes v2', () => {
  const store = fakeStore({ allowedWebsites: ['www.google.com'] })
  const first = loadAllowlist(store)
  assert.deepEqual(loadAllowlist(store), first)
  assert.deepEqual(store.data.allowedWebsites, ['www.google.com'])
  store.data.allowlist.websites[0].allowSubdomains = true
  assert.equal(loadAllowlist(store).websites[0].allowSubdomains, true)
})

test('failed writes and corrupt policies never discard or fall back to legacy state', () => {
  const store = fakeStore({ allowedWebsites: ['example.com'] })
  store.set = () => { throw new Error('disk full') }
  assert.throws(() => loadAllowlist(store), /disk full/)
  assert.deepEqual(store.data, { allowedWebsites: ['example.com'] })
  for (const allowlist of [{ version: 3, websites: [] }, payload([rule('invalid')])]) {
    assert.throws(() => loadAllowlist(fakeStore({ allowlist, allowedWebsites: ['example.com'] })))
  }
  const corruptLegacy = fakeStore({ allowedWebsites: ['example.com', 'invalid'] })
  assert.throws(() => loadAllowlist(corruptLegacy))
  assert.equal(corruptLegacy.has('allowlist'), false)
})

test('unchanged editor preserves settings, removes rules, defaults new domains to false', () => {
  const current = payload([rule('example.com', true), rule('remove.com', true)])
  assert.deepEqual(fromLegacyEditor(['www.example.com', 'http://new.com'], current), payload([rule('example.com', true), rule('new.com')]))
  assert.deepEqual(current.websites[1], rule('remove.com', true))
})
