$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$proj=Join-Path $root 'FT857DControl\FT857DControl.csproj'
$out=Join-Path $root 'publish-win-x64'
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out
Write-Host "Built HamTech M0FXB IC-706MKIIG Controller: $out"
