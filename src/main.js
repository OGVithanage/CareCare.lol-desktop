import { app, BrowserWindow, ipcMain, session } from 'electron/main'
import Store from 'electron-store'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { applyWindowsAllowlist } from './windows-service.js'
import { loadAllowlist, normalizeAllowlist, fromLegacyEditor } from './allowlist.js'

const currentDirectory = path.dirname(fileURLToPath(import.meta.url))
let mainWindow

const store = new Store({
  schema: {
    allowlist: {
      type: 'object',
      required: ['version', 'websites'],
      additionalProperties: false,
      properties: {
        version: { type: 'integer', const: 2 },
        websites: { type: 'array', maxItems: 500, items: {
          type: 'object', required: ['domain', 'allowSubdomains'], additionalProperties: false,
          properties: { domain: { type: 'string', maxLength: 253 }, allowSubdomains: { type: 'boolean' } }
        } }
      }
    },
    allowedWebsites: {
      type: 'array',
      uniqueItems: true,
      maxItems: 500,
      items: { type: 'string', maxLength: 2048 },
      default: []
    }
  }
})

const validateSender = (event) => (
  mainWindow !== undefined &&
  event.sender === mainWindow.webContents &&
  event.senderFrame === event.sender.mainFrame
)

const createWindow = () => {
  mainWindow = new BrowserWindow({
    width: 1000,
    height: 760,
    minWidth: 720,
    minHeight: 620,
    show: false,
    title: 'CareCare.lol',
    backgroundColor: '#000000',
    webPreferences: {
      preload: path.join(currentDirectory, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  })

  mainWindow.loadFile(path.join(currentDirectory, 'index.html'))
  mainWindow.once('ready-to-show', () => mainWindow.show())
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  mainWindow.webContents.on('will-navigate', (event, navigationUrl) => {
    if (navigationUrl !== mainWindow.webContents.getURL()) event.preventDefault()
  })
}

let saveQueue = Promise.resolve()
const saveAndApply = (input) => {
  const operation = saveQueue.then(async () => {
    const current = loadAllowlist(store)
    const allowlist = input === undefined ? current : Array.isArray(input)
      ? fromLegacyEditor(input, current) : normalizeAllowlist(input)
    store.set('allowlist', allowlist)
    return {
      websites: allowlist.websites.map(rule => `https://${rule.domain}`),
      allowlist,
      enforcement: await applyWindowsAllowlist(allowlist)
    }
  })
  saveQueue = operation.catch(() => {})
  return operation
}

app.whenReady().then(() => {
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false))

  ipcMain.handle('allowlist:get', (event) => {
    if (!validateSender(event)) throw new Error('Unauthorized IPC sender')
    return saveAndApply()
  })

  ipcMain.handle('allowlist:save', (event, websites) => {
    if (!validateSender(event)) throw new Error('Unauthorized IPC sender')
    return saveAndApply(websites)
  })

  createWindow()
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit()
})
