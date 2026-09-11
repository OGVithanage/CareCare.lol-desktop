const form = document.querySelector('#website-form')
const input = document.querySelector('#website-input')
const list = document.querySelector('#website-list')
const count = document.querySelector('#website-count')
const emptyState = document.querySelector('#empty-state')
const saveButton = document.querySelector('#save-button')
const status = document.querySelector('#save-status')

let websites = []

const normalizeWebsite = (value) => {
  const candidate = value.trim()
  const url = new URL(candidate.includes('://') ? candidate : `https://${candidate}`)
  if (url.protocol !== 'https:' && url.protocol !== 'http:') throw new Error('Unsupported protocol')
  return url.origin
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
    removeButton.setAttribute('aria-label', `Remove ${website}`)
    removeButton.addEventListener('click', () => {
      websites = websites.filter((entry) => entry !== website)
      status.textContent = 'Unsaved changes'
      renderWebsites()
    })

    item.append(label, removeButton)
    list.append(item)
  })
}

form.addEventListener('submit', (event) => {
  event.preventDefault()
  input.setCustomValidity('')

  try {
    const website = normalizeWebsite(input.value)
    if (!websites.includes(website)) websites.push(website)
    websites.sort((first, second) => first.localeCompare(second))
    input.value = ''
    status.textContent = 'Unsaved changes'
    renderWebsites()
    input.focus()
  } catch {
    input.setCustomValidity('Enter a valid website, such as example.com.')
    input.reportValidity()
  }
})

saveButton.addEventListener('click', async () => {
  saveButton.disabled = true
  status.textContent = 'Saving…'

  try {
    websites = await window.carecare.saveAllowedWebsites(websites)
    status.textContent = 'Saved on this computer'
    renderWebsites()
  } catch {
    status.textContent = 'Could not save. Try again.'
  } finally {
    saveButton.disabled = false
  }
})

const loadWebsites = async () => {
  try {
    websites = await window.carecare.getAllowedWebsites()
    status.textContent = 'Saved on this computer'
    renderWebsites()
  } catch {
    status.textContent = 'Could not load saved websites.'
  }
}

loadWebsites()
