import { normalizeAllowlist } from './allowlist.js'
import net from 'node:net'

export const PIPE_PATH = '\\\\.\\pipe\\CareCare.Allowlist.v2'

// One bounded, newline-delimited JSON request per connection.
export function applyWindowsAllowlist(payload, { platform = process.platform, connect = net.createConnection, timeout = 120000 } = {}) {
  const allowlist = normalizeAllowlist(payload)
  if (platform !== 'win32') return Promise.resolve({ applied: false, message: 'Saved locally. Network filtering requires Windows.' })
  return new Promise((resolve) => {
    let socket
    let response = ''
    let finished = false
    const finish = (result) => {
      if (finished) return
      finished = true
      clearTimeout(timer)
      socket?.destroy()
      resolve(result)
    }
    const fail = (message) => finish({ applied: false, message: `Saved locally; Windows update not confirmed. ${message}` })
    const timer = setTimeout(() => fail('Service timed out. Retry to confirm the current list.'), timeout)
    try {
      socket = connect(PIPE_PATH)
      socket.setEncoding('utf8')
      socket.once('connect', () => socket.write(`${JSON.stringify(allowlist)}\n`))
      socket.on('data', (chunk) => {
        response += chunk
        if (Buffer.byteLength(response) > 65536) return fail('Invalid service response.')
        const end = response.indexOf('\n')
        if (end === -1) return
        try {
          const result = JSON.parse(response.slice(0, end))
          if (result.version !== 2) return fail('Service upgrade required: install the version 2 Windows service.')
          if (typeof result.applied !== 'boolean' || typeof result.message !== 'string') throw new Error()
          finish({ applied: result.applied, message: result.message })
        } catch { fail('Invalid service response.') }
      })
      socket.once('error', () => fail('Install or upgrade to the version 2 CareCare service; check that it is running and your account is authorized.'))
      socket.once('end', () => fail('Service closed the connection.'))
    } catch { fail('Could not connect to the service.') }
  })
}
