import test from 'node:test'
import assert from 'node:assert/strict'
import { upsertRule, setRuleSubdomains, removeRule } from '../src/allowlist-editor.js'
const initial = { version: 2, websites: [{ domain: 'example.com', allowSubdomains: false }, { domain: 'z.com', allowSubdomains: true }] }

test('upserts normalized variants immutably, preserves other rules and sorts', () => {
  for (const domain of ['www.example.com', 'http://EXAMPLE.com/path', 'https://example.com:443/a']) {
    const next = upsertRule(initial, domain, true)
    assert.deepEqual(next.websites, [{ domain: 'example.com', allowSubdomains: true }, initial.websites[1]])
  }
  assert.equal(initial.websites[0].allowSubdomains, false)
  assert.deepEqual(upsertRule(initial, 'a.com', false).websites.map(r => r.domain), ['a.com', 'example.com', 'z.com'])
})
test('row operations use normalized identity and strict booleans', () => {
  assert.equal(setRuleSubdomains(initial, 'www.example.com', true).websites[0].allowSubdomains, true)
  assert.deepEqual(removeRule(initial, 'https://www.example.com/path').websites, [initial.websites[1]])
  for (const value of [undefined, 'true', 1, null]) {
    assert.throws(() => upsertRule(initial, 'a.com', value))
    assert.throws(() => setRuleSubdomains(initial, 'example.com', value))
  }
  assert.throws(() => setRuleSubdomains(initial, 'missing.com', true))
})
test('500 rule limit permits edits but rejects additions', () => {
  const full = { version: 2, websites: Array.from({ length: 500 }, (_, i) => ({ domain: `site${i}.com`, allowSubdomains: false })) }
  assert.equal(upsertRule(full, 'site0.com', true).websites.length, 500)
  assert.throws(() => upsertRule(full, 'extra.com', true))
})
