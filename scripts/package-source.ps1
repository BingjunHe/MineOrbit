$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Force artifacts | Out-Null
$zipPath = Join-Path $projectRoot 'artifacts/MineOrbit-source.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(Get-ChildItem -LiteralPath $projectRoot -File -Force)
    foreach ($folder in @('src','tests','tools/MineOrbit.Smoke','tools/MineOrbit.Branding','assets','scripts','docs','licenses','.github')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
    foreach ($file in $files) {
        $entry = [System.IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
    }
}
finally { $archive.Dispose() }
Get-Item -LiteralPath $zipPath | Select-Object FullName,Length
