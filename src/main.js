import { app, BrowserWindow, ipcMain, session } from 'electron/main'
import Store from 'electron-store'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const currentDirectory = path.dirname(fileURLToPath(import.meta.url))
let mainWindow

const store = new Store({
  schema: {
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

const normalizeWebsite = (value) => {
  if (typeof value !== 'string' || value.length > 2048) throw new Error('Invalid website')
  const candidate = value.trim()
  if (!candidate) throw new Error('Website cannot be empty')

  const url = new URL(candidate.includes('://') ? candidate : `https://${candidate}`)
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    throw new Error('Only HTTP and HTTPS websites are supported')
  }

  return url.origin
}

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

app.whenReady().then(() => {
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false))

  ipcMain.handle('allowlist:get', (event) => {
    if (!validateSender(event)) throw new Error('Unauthorized IPC sender')
    return store.get('allowedWebsites')
  })

  ipcMain.handle('allowlist:save', (event, websites) => {
    if (!validateSender(event)) throw new Error('Unauthorized IPC sender')
    if (!Array.isArray(websites) || websites.length > 500) throw new Error('Invalid website list')

    const normalizedWebsites = [...new Set(websites.map(normalizeWebsite))]
    store.set('allowedWebsites', normalizedWebsites)
    return normalizedWebsites
  })

  createWindow()
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit()
})
