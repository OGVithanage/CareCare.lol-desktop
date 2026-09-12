import { normalizeWebsite } from './website.js'

export const normalizeRule = (rule) => {
  if (!rule || typeof rule !== 'object' || Array.isArray(rule) || typeof rule.allowSubdomains !== 'boolean') {
    throw new Error('A website rule requires a domain and boolean allowSubdomains')
  }
  return { domain: normalizeWebsite(rule.domain).slice(8), allowSubdomains: rule.allowSubdomains }
}

export const normalizeAllowlist = (payload) => {
  if (!payload || payload.version !== 2 || !Array.isArray(payload.websites) || payload.websites.length > 500) {
    throw new Error('Invalid allowlist or unsupported version; version 2 is required')
  }
  const rules = new Map()
  for (const value of payload.websites) {
    const rule = normalizeRule(value)
    rules.set(rule.domain, rule)
  }
  return { version: 2, websites: [...rules.values()].sort((a, b) => a.domain.localeCompare(b.domain)) }
}

export const migrateLegacy = (websites) => {
  if (!Array.isArray(websites) || websites.length > 500) throw new Error('Invalid legacy allowlist')
  return normalizeAllowlist({ version: 2, websites: websites.map(domain => ({ domain, allowSubdomains: false })) })
}

export const loadAllowlist = (store) => {
  // A present but corrupt/unknown policy is an error, never an unrestricted reset
  // or a fallback to a potentially broader, stale legacy policy.
  if (store.has('allowlist')) return normalizeAllowlist(store.get('allowlist'))
  const migrated = migrateLegacy(store.get('allowedWebsites', []))
  store.set('allowlist', migrated)
  // Retain legacy data as a migration backup; the versioned policy takes precedence.
  return migrated
}

// Compatibility for the unchanged renderer. Preserve settings for retained rows;
// only genuinely new domains receive the migration default of false.
export const fromLegacyEditor = (websites, current) => {
  const normalized = migrateLegacy(websites)
  const existing = new Map(current.websites.map(rule => [rule.domain, rule.allowSubdomains]))
  return normalizeAllowlist({ version: 2, websites: normalized.websites.map(rule => ({
    ...rule, allowSubdomains: existing.get(rule.domain) ?? false
  })) })
}
