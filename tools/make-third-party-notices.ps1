$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$bridgeRoot = Join-Path $projectRoot 'bridge'
$licenseOutput = Join-Path $projectRoot 'THIRD-PARTY-LICENSES'
$noticeOutput = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'

Push-Location $bridgeRoot
try {
    $metadataText = cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1 2>$null | Out-String
    $metadata = $metadataText | ConvertFrom-Json
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
$resolvedLicenseOutput = (Resolve-Path -LiteralPath $licenseOutput).Path
if ($resolvedLicenseOutput -ne (Join-Path $projectRoot 'THIRD-PARTY-LICENSES')) {
    throw "Unexpected license output directory: $resolvedLicenseOutput"
}
Get-ChildItem -LiteralPath $licenseOutput -File | Remove-Item -Force
$rows = [System.Collections.Generic.List[string]]::new()
$rows.Add('# Third-party notices')
$rows.Add('')
$rows.Add('The USB bridge is built from the Rust packages recorded in `bridge/Cargo.lock` for `x86_64-pc-windows-msvc`. Cargo metadata supplies the license expressions below. License texts found in each package source archive are included in `THIRD-PARTY-LICENSES`.')
$rows.Add('')
$rows.Add('| Package | Version | License expression | Included license files |')
$rows.Add('| --- | --- | --- | --- |')

$bridgeLicenseName = 'apple-device-bridge-0.1.0-LICENSE'
Copy-Item -LiteralPath (Join-Path $bridgeRoot 'LICENSE') -Destination (Join-Path $licenseOutput $bridgeLicenseName) -Force
$rows.Add('| `apple-device-bridge` | `0.1.0` | `MIT` | {0} |' -f $bridgeLicenseName)

foreach ($package in ($metadata.packages | Where-Object { $_.name -ne 'apple-device-bridge' } | Sort-Object name, version)) {
    $sourceDirectory = Split-Path -Parent $package.manifest_path
    $matches = @(Get-ChildItem -LiteralPath $sourceDirectory -File | Where-Object {
        $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE)([-.].*)?$'
    })
    if ($matches.Count -eq 0 -and $package.name -in @('idevice', 'plist-macro')) {
        $fallbackName = '{0}-{1}-LICENSE.txt' -f $package.name, $package.version
        $fallbackPath = Join-Path (Join-Path $bridgeRoot 'third-party') $fallbackName
        if (Test-Path -LiteralPath $fallbackPath) {
            $matches = @(Get-Item -LiteralPath $fallbackPath)
        }
    }
    $copiedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($licenseFile in $matches) {
        $safePackageName = $package.name -replace '[^A-Za-z0-9._+-]', '_'
        $safeVersion = $package.version -replace '[^A-Za-z0-9._+-]', '_'
        if ($licenseFile.DirectoryName -eq (Join-Path $bridgeRoot 'third-party')) {
            $destinationName = $licenseFile.Name
        }
        else {
            $destinationName = '{0}-{1}-{2}' -f $safePackageName, $safeVersion, $licenseFile.Name
        }
        Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $licenseOutput $destinationName) -Force
        $destinationPath = Join-Path $licenseOutput $destinationName
        Set-ItemProperty -LiteralPath $destinationPath -Name LastWriteTime -Value (Get-Date)
        $copiedNames.Add($destinationName)
    }
    $licenseExpression = if ($package.license) { $package.license } else { 'not declared in Cargo metadata' }
    $licenseNames = if ($copiedNames.Count -gt 0) { ($copiedNames -join '<br>') } else { 'No top-level license text found in package source' }
    $row = '| `{0}` | `{1}` | `{2}` | {3} |' -f @($package.name, $package.version, $licenseExpression, $licenseNames)
    $rows.Add($row)
}

Set-Content -LiteralPath $noticeOutput -Value $rows -Encoding utf8
