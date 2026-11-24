param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $project 'release'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetTempPath()) ('ILPatchStudio-release-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $work 'bundle'
$sample = Join-Path $work 'sample'

dotnet publish (Join-Path $project 'ILPatchStudio.csproj') -c Release -r win-x64 --self-contained true -o $bundle
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
dotnet build (Join-Path $project 'samples/PriceDemo/PriceDemo.csproj') -c Release -o $sample
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }

$null = New-Item -ItemType Directory -Path (Join-Path $bundle 'samples')
Copy-Item -LiteralPath (Join-Path $sample 'PriceDemo.dll') -Destination (Join-Path $bundle 'samples')
$licenses = Join-Path $bundle 'licenses'
$null = New-Item -ItemType Directory -Path $licenses

$assets = Get-Content -Raw -LiteralPath (Join-Path $project 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
$runtimePacks = @($assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies })
foreach ($name in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
    $pack = $runtimePacks | Where-Object name -eq $name | Select-Object -First 1
    if (!$pack -or $pack.version -notmatch '^\[([^,]+),\s*\1\]$') {
        throw "No exact runtime pack version in restored assets: $name"
    }
    $relativePath = "$($name.ToLowerInvariant())/$($Matches[1])"
    $folder = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $relativePath } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (!$folder) { throw "Package not present in restored package folders: $name" }
    $notices = @(Get-ChildItem -LiteralPath $folder -File |
        Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' })
    if ($notices.Count -eq 0) { throw "No redistribution notices found for $name" }
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses "$name-$($notice.Name)")
    }
}

$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $bundle 'ILPatchStudio.dll')).Version.ToString(3)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$zip = Join-Path $OutputDirectory "ILPatchStudio-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw "Archive already exists: $zip" }
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $zip -Algorithm SHA256
