const { contextBridge } = require('electron/renderer')

contextBridge.exposeInMainWorld('carecare', {
  versions: Object.freeze({
    electron: process.versions.electron,
    chrome: process.versions.chrome
  })
})
