import { normalizeAllowlist, normalizeRule } from './allowlist.js'
import { normalizeWebsite } from './website.js'

export const upsertRule = (allowlist, domain, allowSubdomains) => {
  const current = normalizeAllowlist(allowlist)
  const rule = normalizeRule({ domain, allowSubdomains })
  return normalizeAllowlist({ version: 2, websites: [
    ...current.websites.filter(entry => entry.domain !== rule.domain), rule
  ] })
}

export const setRuleSubdomains = (allowlist, domain, allowSubdomains) => {
  const rule = normalizeRule({ domain, allowSubdomains })
  const current = normalizeAllowlist(allowlist)
  if (!current.websites.some(entry => entry.domain === rule.domain)) throw new Error('Website not found')
  return upsertRule(current, rule.domain, rule.allowSubdomains)
}

export const removeRule = (allowlist, domain) => {
  const normalized = normalizeWebsite(domain).slice(8)
  const current = normalizeAllowlist(allowlist)
  return { version: 2, websites: current.websites.filter(rule => rule.domain !== normalized) }
}
