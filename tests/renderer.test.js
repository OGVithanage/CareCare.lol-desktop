import test from 'node:test'
import assert from 'node:assert/strict'
import { initializeRenderer } from '../src/renderer.js'

class Element {
  constructor(tag, document) { this.tag = tag; this.document = document; this.children = []; this.events = {}; this.attrs = {}; this.dataset = {}; this.checked = false; this.value = ''; }
  append(...nodes) { this.children.push(...nodes) }
  replaceChildren() { this.children = [] }
  setAttribute(key, value) { this.attrs[key] = value }
  addEventListener(type, fn) { this.events[type] = fn }
  querySelectorAll(tag) { return this.children.flatMap(child => [...(child.tag === tag ? [child] : []), ...child.querySelectorAll(tag)]) }
  querySelector(tag) { return this.querySelectorAll(tag)[0] }
  fire(type) { return this.events[type]?.({ preventDefault() {} }) }
  setCustomValidity(value) { this.validity = value }
  reportValidity() {}
  focus() { this.document.activeElement = this }
}
const policy = { version: 2, websites: [{ domain: 'example.com', allowSubdomains: false }, { domain: 'z.com', allowSubdomains: true }] }
const flush = () => new Promise(resolve => setImmediate(resolve))
async function setup(save) {
  const document = { createElement(tag) { return new Element(tag, this) }, createElementNS(ns, tag) { return this.createElement(tag) } }
  document.body = document.createElement('body'); document.activeElement = document.body
  const nodes = Object.fromEntries(['website-form', 'website-input', 'allow-subdomains', 'website-list', 'website-count', 'empty-state', 'save-button', 'save-status', 'add'].map(id => [id, document.createElement(id === 'add' ? 'button' : 'div')]))
  document.querySelector = selector => nodes[selector === '.add-button' ? 'add' : selector.slice(1)]
  const calls = []
  await initializeRenderer(document, {
    getAllowedWebsites: async () => ({ allowlist: structuredClone(policy), websites: ['wrong.com'], enforcement: { message: 'Loaded' } }),
    saveAllowedWebsites: async payload => { calls.push(structuredClone(payload)); return save(payload) }
  })
  return { nodes, calls, buttons: () => nodes['website-list'].querySelectorAll('button') }
}
test('loads v2 accessible states; toggles full snapshot; locks controls until canonical response', async () => {
  let finish
  const app = await setup(() => new Promise(resolve => { finish = resolve }))
  const buttons = app.buttons()
  assert.equal(buttons[0].attrs['aria-pressed'], 'false')
  assert.equal(buttons[2].attrs['aria-pressed'], 'true')
  assert.equal(buttons[0].attrs['aria-label'], 'Additional subdomains for example.com: blocked')
  assert.equal(buttons[0].children[1].textContent, 'Additional subdomains blocked')
  assert.equal(buttons[0].className, 'subdomain-button')
  assert.notEqual(buttons[0].children[0].children[0].attrs.d, buttons[2].children[0].children[0].attrs.d)
  buttons[0].fire('click')
  buttons[0].fire('click')
  assert.equal(app.calls.length, 1)
  assert.deepEqual(app.calls[0], { version: 2, websites: [{ domain: 'example.com', allowSubdomains: true }, policy.websites[1]] })
  for (const key of ['website-input', 'allow-subdomains', 'add', 'save-button']) assert.equal(app.nodes[key].disabled, true)
  assert.ok(app.buttons().every(button => button.disabled))
  assert.equal(app.nodes['save-status'].textContent, 'Saving and applying…')
  finish({ allowlist: { version: 2, websites: [policy.websites[1]] }, enforcement: { applied: true, message: 'Applied' } })
  await flush()
  assert.equal(app.buttons().length, 2)
  assert.equal(app.nodes['save-status'].textContent, 'Applied')
  assert.equal(app.nodes['allow-subdomains'].disabled, false)
})
test('form upsert resets checkbox; identical add avoids save; remove uses domain', async () => {
  const app = await setup(async payload => ({ allowlist: payload, enforcement: { applied: true, message: 'Applied' } }))
  app.nodes['website-input'].value = 'https://www.example.com/path'
  app.nodes['allow-subdomains'].checked = true
  app.nodes['website-form'].fire('submit')
  assert.equal(app.nodes['website-input'].value, '')
  assert.equal(app.nodes['allow-subdomains'].checked, false)
  await flush()
  app.nodes['website-input'].value = 'example.com'
  app.nodes['allow-subdomains'].checked = true
  app.nodes['website-form'].fire('submit')
  assert.equal(app.calls.length, 1)
  app.buttons()[1].fire('click')
  await flush()
  assert.deepEqual(app.calls[1].websites, [policy.websites[1]])
})
test('service rejection and IPC failure retain desired policy for Retry', async () => {
  let fail = false
  const app = await setup(async payload => {
    if (fail) throw new Error('IPC failed')
    return { allowlist: payload, enforcement: { applied: false, message: 'Service stopped; enforcement not confirmed.' } }
  })
  app.buttons()[0].fire('click'); await flush()
  assert.equal(app.nodes['save-status'].textContent, 'Service stopped; enforcement not confirmed.')
  assert.equal(app.buttons()[0].attrs['aria-pressed'], 'true')
  fail = true
  app.nodes['save-button'].fire('click'); await flush()
  assert.equal(app.nodes['save-status'].textContent, 'Could not save changes. Retry.')
  assert.equal(app.nodes['save-button'].disabled, false)
  app.nodes['save-button'].fire('click'); await flush()
  assert.deepEqual(app.calls[2], app.calls[0])
})
