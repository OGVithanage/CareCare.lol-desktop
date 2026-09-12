export const normalizeWebsite = (value) => {
  if (typeof value !== 'string' || value.length > 2048) throw new Error('Invalid website')
  const candidate = value.trim()
  if (!candidate) throw new Error('Website cannot be empty')
  const url = new URL(candidate.includes('://') ? candidate : `https://${candidate}`)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
    throw new Error('Only HTTP and HTTPS websites without credentials are supported')
  }
  const host = url.hostname.replace(/\.$/, '')
  if (host.length > 253 || !host.includes('.') || /^[\d.]+$/.test(host) ||
      host.split('.').some((label) => !/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(label))) {
    throw new Error('Enter a DNS domain; IP literals and wildcards are unsupported')
  }
  // Rules describe domains, not individual schemes, paths or ports.
  // Treat one conventional www prefix as an alias without widening other hosts.
  const domain = host.startsWith('www.') && host.slice(4).includes('.') ? host.slice(4) : host
  return `https://${domain}`
}
