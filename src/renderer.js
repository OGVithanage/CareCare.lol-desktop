import { normalizeWebsite } from './website.js'

const form = document.querySelector('#website-form')
const input = document.querySelector('#website-input')
const list = document.querySelector('#website-list')
const count = document.querySelector('#website-count')
const emptyState = document.querySelector('#empty-state')
const saveButton = document.querySelector('#save-button')
const status = document.querySelector('#save-status')

let websites = []
let busy = true
let loaded = false

const setBusy = (value) => {
  busy = value
  input.disabled = value
  form.querySelector('button').disabled = value
  saveButton.disabled = value
  list.querySelectorAll('button').forEach((button) => { button.disabled = value })
}

const applyChanges = async () => {
  setBusy(true)
  status.textContent = 'Saving and applying…'
  try {
    const result = await window.carecare.saveAllowedWebsites(websites)
    websites = result.websites
    status.textContent = result.enforcement.message
  } catch {
    status.textContent = 'Could not save changes. Retry.'
  } finally {
    renderWebsites()
    setBusy(false)
  }
}

const renderWebsites = () => {
  list.replaceChildren()
  count.textContent = `${websites.length} ${websites.length === 1 ? 'website' : 'websites'}`
  emptyState.hidden = websites.length !== 0

  websites.forEach((website) => {
    const item = document.createElement('li')
    const label = document.createElement('span')
    const removeButton = document.createElement('button')

    label.textContent = website
    removeButton.type = 'button'
    removeButton.className = 'remove-button'
    removeButton.textContent = 'Remove'
    removeButton.disabled = busy
    removeButton.setAttribute('aria-label', `Remove ${website}`)
    removeButton.addEventListener('click', () => {
      websites = websites.filter((entry) => entry !== website)
      renderWebsites()
      void applyChanges()
    })

    item.append(label, removeButton)
    list.append(item)
  })
}

form.addEventListener('submit', (event) => {
  event.preventDefault()
  if (busy) return
  input.setCustomValidity('')

  try {
    const website = normalizeWebsite(input.value)
    if (!websites.includes(website)) websites.push(website)
    websites.sort((first, second) => first.localeCompare(second))
    input.value = ''
    renderWebsites()
    void applyChanges()
  } catch {
    input.setCustomValidity('Enter a valid website, such as example.com.')
    input.reportValidity()
  }
})

saveButton.addEventListener('click', () => loaded ? applyChanges() : loadWebsites())

const loadWebsites = async () => {
  setBusy(true)
  try {
    const result = await window.carecare.getAllowedWebsites()
    websites = result.websites
    loaded = true
    status.textContent = result.enforcement.message
    renderWebsites()
    setBusy(false)
  } catch {
    status.textContent = 'Could not load saved websites. Retry to load them.'
    saveButton.disabled = false
  }
}

setBusy(true)
loadWebsites()
