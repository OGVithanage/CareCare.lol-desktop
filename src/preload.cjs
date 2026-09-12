const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('carecare', {
  getAllowedWebsites: () => ipcRenderer.invoke('allowlist:get'),
  saveAllowedWebsites: (allowlist) => ipcRenderer.invoke('allowlist:save', allowlist)
})
