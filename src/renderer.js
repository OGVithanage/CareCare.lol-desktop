const electronVersion = document.querySelector('[data-electron-version]')

if (electronVersion && window.carecare?.versions?.electron) {
  electronVersion.textContent = `Electron ${window.carecare.versions.electron}`
}
