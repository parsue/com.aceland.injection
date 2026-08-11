$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet build -c Release
New-Item -ItemType Directory -Force -Path ..\Analyzers | Out-Null
Copy-Item bin\Release\netstandard2.0\AceLand.Injection.SourceGenerator.dll ..\Analyzers\ -Force

Write-Host "✔ copied to ../Analyzers/" -ForegroundColor Green