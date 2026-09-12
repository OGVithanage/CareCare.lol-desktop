import { upsertRule, setRuleSubdomains, removeRule } from './allowlist-editor.js'

export const initializeRenderer = (document, carecare) => {
  const form = document.querySelector('#website-form')
  const input = document.querySelector('#website-input')
  const addButton = document.querySelector('.add-button')
  const checkbox = document.querySelector('#allow-subdomains')
  const list = document.querySelector('#website-list')
  const count = document.querySelector('#website-count')
  const emptyState = document.querySelector('#empty-state')
  const saveButton = document.querySelector('#save-button')
  const status = document.querySelector('#save-status')
  let allowlist = { version: 2, websites: [] }
  let busy = true
  let loaded = false

  const setBusy = (value) => {
    busy = value
    for (const control of [input, addButton, checkbox, saveButton, ...list.querySelectorAll('button')]) control.disabled = value
  }

  const renderWebsites = () => {
    list.replaceChildren()
    count.textContent = `${allowlist.websites.length} ${allowlist.websites.length === 1 ? 'website' : 'websites'}`
    emptyState.hidden = allowlist.websites.length !== 0
    for (const rule of allowlist.websites) {
      const item = document.createElement('li')
      const label = document.createElement('span')
      label.className = 'website-label'
      label.textContent = `https://${rule.domain}`
      const actions = document.createElement('div')
      actions.className = 'website-actions'
      const toggle = document.createElement('button')
      toggle.type = 'button'
      toggle.className = 'subdomain-button'
      toggle.disabled = busy
      toggle.setAttribute('aria-pressed', String(rule.allowSubdomains))
      toggle.setAttribute('aria-label', `Additional subdomains for ${rule.domain}: ${rule.allowSubdomains ? 'allowed' : 'blocked'}`)
      const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg')
      svg.setAttribute('viewBox', '0 0 24 24')
      svg.setAttribute('aria-hidden', 'true')
      svg.setAttribute('focusable', 'false')
      const path = document.createElementNS('http://www.w3.org/2000/svg', 'path')
      path.setAttribute('d', rule.allowSubdomains
        ? 'M12 7v5M5 17v-5h14v5M9 2h6v5H9zM2 17h6v5H2zM16 17h6v5h-6z'
        : 'M9 2h6v5H9zM2 17h6v5H2zM16 17h6v5h-6zM3 3l18 18')
      svg.append(path)
      const tooltip = document.createElement('span')
      tooltip.className = 'row-tooltip'
      tooltip.setAttribute('role', 'tooltip')
      tooltip.textContent = `Additional subdomains ${rule.allowSubdomains ? 'allowed' : 'blocked'}`
      toggle.append(svg, tooltip)
      toggle.addEventListener('click', () => {
        if (busy) return
        allowlist = setRuleSubdomains(allowlist, rule.domain, !rule.allowSubdomains)
        renderWebsites()
        void applyChanges({ domain: rule.domain, action: 'toggle' })
      })
      const remove = document.createElement('button')
      remove.type = 'button'
      remove.className = 'remove-button'
      remove.textContent = 'Remove'
      remove.disabled = busy
      remove.setAttribute('aria-label', `Remove ${rule.domain}`)
      remove.addEventListener('click', () => {
        if (busy) return
        allowlist = removeRule(allowlist, rule.domain)
        renderWebsites()
        void applyChanges({ action: 'remove' })
      })
      item.dataset.domain = rule.domain
      actions.append(toggle, remove)
      item.append(label, actions)
      list.append(item)
    }
  }

  const applyChanges = async (focus) => {
    setBusy(true)
    status.textContent = 'Saving and applying…'
    try {
      const result = await carecare.saveAllowedWebsites(allowlist)
      allowlist = result.allowlist
      status.textContent = result.enforcement.message
    } catch {
      status.textContent = 'Could not save changes. Retry.'
    } finally {
      renderWebsites()
      setBusy(false)
      // Rebuilding rows must not strand keyboard focus on the document body.
      if (focus && (!document.activeElement || document.activeElement === document.body)) {
        const row = [...list.children].find(item => item.dataset.domain === focus.domain)
        const target = focus.action === 'toggle' ? row?.querySelector('button') : input
        target?.focus()
      }
    }
  }

  form.addEventListener('submit', (event) => {
    event.preventDefault()
    if (busy) return
    input.setCustomValidity('')
    try {
      const next = upsertRule(allowlist, input.value, checkbox.checked)
      const changed = JSON.stringify(next) !== JSON.stringify(allowlist)
      allowlist = next
      input.value = ''
      checkbox.checked = false
      renderWebsites()
      if (changed) void applyChanges()
    } catch (error) {
      input.setCustomValidity(error.message.includes('version 2') ? 'You can save up to 500 websites.' : 'Enter a valid website, such as example.com.')
      input.reportValidity()
    }
  })
  input.addEventListener('input', () => input.setCustomValidity(''))

  const loadWebsites = async () => {
    setBusy(true)
    try {
      const result = await carecare.getAllowedWebsites()
      allowlist = result.allowlist
      loaded = true
      status.textContent = result.enforcement.message
      renderWebsites()
      setBusy(false)
    } catch {
      status.textContent = 'Could not load saved websites. Retry to load them.'
      saveButton.disabled = false
    }
  }
  saveButton.addEventListener('click', () => {
    if (loaded && !busy) void applyChanges()
    else if (!loaded && !saveButton.disabled) void loadWebsites()
  })
  setBusy(true)
  return loadWebsites()
}

if (typeof document !== 'undefined') void initializeRenderer(document, window.carecare)
