using System.Globalization;
using System.Net;

namespace CareCare;

internal static class DomainPolicy
{
    internal static string[] Normalize(string[]? websites)
    {
        if (websites is null || websites.Length > 500) throw new ArgumentException("At most 500 websites are supported.");
        return websites.Select(value =>
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) throw new ArgumentException("Invalid website.");
            var candidate = value.Trim();
            if (!candidate.Contains("://")) candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0)
                throw new ArgumentException("Only HTTP and HTTPS websites without credentials are supported.");
            var host = new IdnMapping().GetAscii(uri.DnsSafeHost.TrimEnd('.')).ToLowerInvariant();
            if (IPAddress.TryParse(host.Trim('[', ']'), out _) || host.Length > 253 ||
                !host.Contains('.') || host.Split('.').Any(label => label.Length is < 1 or > 63 ||
                    label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new ArgumentException("Enter a DNS domain, such as example.com; IP literals and wildcards are unsupported.");
            return host;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
