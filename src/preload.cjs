const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('carecare', {
  getAllowedWebsites: () => ipcRenderer.invoke('allowlist:get'),
  saveAllowedWebsites: (websites) => ipcRenderer.invoke('allowlist:save', websites)
})
