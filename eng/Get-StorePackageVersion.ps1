param([string]$Version)

$ErrorActionPreference = 'Stop'

# Store package versions are numeric build numbers. A prerelease suffix must never
# be discarded: beta.1 and beta.2 would otherwise produce the same MSIX identity.
if ($Version -cnotmatch '\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\.0\z') {
    throw "Store package version '$Version' must use Major.Minor.Build.0 (for example 1.1.1.0), without prerelease or build metadata suffixes."
}

$parts = @($Version.Split('.') | ForEach-Object { [int]$_ })
if ($parts[0] -eq 0 -or @($parts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "Store package version '$Version' must have a major version of 1-65535 and other components of 0-65535; the final component is reserved for Microsoft Store and must be 0."
}

$parts -join '.'
