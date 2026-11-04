# Public Windows installation media

If the user already supplied an ISO, use it without a provenance interview.
Only compare hashes when requested or given a trusted expected value; report
and resolve a mismatch before conversion. A computed fingerprint alone does
not prove authenticity.

For a new download, start with the
[Windows Insider Preview ISO page](https://www.microsoft.com/en-us/software-download/windowsinsiderpreviewiso).
Verified 2026-11-04: it requires Windows Insider Program membership and
Microsoft account sign-in. Let the user sign in and enroll in the browser;
never collect passwords, tokens or cookies, capture an authenticated session,
scrape around sign-in, or invent a signed CDN link.

Explain that Insider releases are previews: they can regress, expire, or lack
support for a specific feature. Choose an offered flight that meets verified
technical requirements rather than assuming the newest build is eligible.
Windows licensing/terms still apply; a download does not grant a license.

When the user declines enrollment or needs stability, offer the official
[released Windows 11 x64 ISO](https://www.microsoft.com/software-download/windows11)
or [released Windows 11 ARM64 ISO](https://www.microsoft.com/software-download/windows11arm64).
For preview ARM64 media, inspect the
[official ARM64 preview page](https://www.microsoft.com/software-download/windowsinsiderpreviewARM64)
and verify the actual offered file type and architecture.

Guide release/edition, language, and architecture selection in the **ISO**
section, not Installation Assistant or an upgrade of this host. Save to a
user-approved local path. Download automation requires permission and must not
export authentication data. Return to the official page for expired links.
Do not promise a specific flight or bypass TPM, Secure Boot, activation, EULA,
or licensing requirements.
