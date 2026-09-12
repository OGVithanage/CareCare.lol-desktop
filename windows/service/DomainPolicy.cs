using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CareCare;

internal sealed record WebsiteRule(string Domain, bool AllowSubdomains);
internal sealed record Allowlist(int Version, WebsiteRule[] Websites);

internal static class DomainPolicy
{
    internal static string Hostname(string value, bool alias = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) throw new ArgumentException("Invalid website.");
        var candidate = value.Trim();
        if (!candidate.Contains("://")) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("Only HTTP and HTTPS websites without credentials are supported.");
        var host = new IdnMapping().GetAscii(uri.DnsSafeHost).ToLowerInvariant();
        if (host.EndsWith('.')) host = host[..^1];
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || host.Length > 253 ||
            !host.Contains('.') || host.Split('.').Any(label => label.Length is < 1 or > 63 ||
                label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Enter a DNS domain; IP literals and wildcards are unsupported.");
        return alias && host.StartsWith("www.", StringComparison.Ordinal) && host[4..].Contains('.') ? host[4..] : host;
    }

    internal static Allowlist Normalize(IEnumerable<WebsiteRule> websites)
    {
        var rules = websites.ToArray();
        if (rules.Length > 500) throw new ArgumentException("At most 500 websites are supported.");
        return new(2, rules.Select(rule => new WebsiteRule(Hostname(rule.Domain, true), rule.AllowSubdomains))
            .GroupBy(rule => rule.Domain, StringComparer.Ordinal).Select(group => group.Last())
            .OrderBy(rule => rule.Domain, StringComparer.Ordinal).ToArray());
    }

    internal static Allowlist Parse(string json, bool allowLegacy = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (allowLegacy && root.ValueKind == JsonValueKind.Array)
            return Normalize(root.EnumerateArray().Select(entry => new WebsiteRule(entry.GetString() ?? throw new ArgumentException("Invalid legacy rule."), false)));
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            !version.TryGetInt32(out var number) || number != 2)
            throw new ArgumentException("Upgrade required: only allowlist protocol version 2 is supported.");
        var entries = root.GetProperty("websites");
        if (entries.ValueKind != JsonValueKind.Array) throw new ArgumentException("Invalid websites.");
        return Normalize(entries.EnumerateArray().Select(entry =>
        {
            var domain = entry.GetProperty("domain").GetString() ?? throw new ArgumentException("Missing domain.");
            var setting = entry.GetProperty("allowSubdomains");
            if (setting.ValueKind != JsonValueKind.True && setting.ValueKind != JsonValueKind.False)
                throw new ArgumentException("allowSubdomains must be a boolean.");
            return new WebsiteRule(domain, setting.GetBoolean());
        }));
    }

    internal static bool Allows(Allowlist policy, string hostname)
    {
        var host = Hostname(hostname);
        return policy.Websites.Any(rule => host == rule.Domain || host == "www." + rule.Domain ||
            (rule.AllowSubdomains && host.EndsWith("." + rule.Domain, StringComparison.Ordinal)));
    }
}
