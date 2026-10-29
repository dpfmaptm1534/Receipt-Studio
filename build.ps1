# Run on Windows with the .NET 8 SDK installed.
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o build-ocr --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host "Built: $PSScriptRoot\build-ocr\ReceiptStudio.exe"
}
finally {
    Pop-Location
}
