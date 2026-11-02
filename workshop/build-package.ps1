# Builds the Steam Workshop upload for Next-Gen Patch Panels into ..\publish\ (gitignored):
#   publish\content\            what players download: Mods\NextGenPatchPanels.dll, INSTALL.txt, LICENSE.txt
#   publish\workshop_item.vdf   SteamCMD's description of the upload
#   publish\upload.cmd          double-click to upload; asks for your Steam account name, SteamCMD asks
#                               for the password and Steam Guard code itself
#
# Usage:  powershell -ExecutionPolicy Bypass -File workshop\build-package.ps1 -ChangeNote "v1.0.1: ..."
# The Workshop item ID lives in workshop\publishedfileid.txt (0 = not uploaded yet, so SteamCMD creates
# a new, private item). Only that first upload sets visibility, so later updates never hide a public page.
param(
    [string]$ChangeNote = "",
    [string]$SteamCmd = (Join-Path $PSScriptRoot "..\..\..\_shared\steamcmd\steamcmd.exe")
)
$ErrorActionPreference = "Stop"
$SteamCmd = [System.IO.Path]::GetFullPath($SteamCmd)
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repo "src\NextGenPatchPanels"
$publish = Join-Path $repo "publish"
$content = Join-Path $publish "content"

dotnet build -c Release $project -p:Deploy=false | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$dll = Join-Path $project "bin\Release\net6.0\NextGenPatchPanels.dll"
$version = (Get-Item $dll).VersionInfo.ProductVersion -replace '\+.*$', ''

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $content "Mods") | Out-Null
Copy-Item $dll (Join-Path $content "Mods")
Copy-Item (Join-Path $PSScriptRoot "INSTALL.txt") $content
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $content "LICENSE.txt")

$id = (Get-Content (Join-Path $PSScriptRoot "publishedfileid.txt") -Raw).Trim()
if ([string]::IsNullOrEmpty($ChangeNote)) { $ChangeNote = "v$version" }
function Vdf([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }
$visibility = if ($id -eq "0") { "`t`"visibility`"`t`"2`"`r`n" } else { "" }  # 2 = private
$noBom = New-Object System.Text.UTF8Encoding $false

$vdf = @"
"workshopitem"
{
	"appid"	"4170200"
	"publishedfileid"	"$id"
	"contentfolder"	"$(Vdf $content)"
	"previewfile"	"$(Vdf (Join-Path $PSScriptRoot 'preview.jpg'))"
$visibility	"title"	"Next-Gen Patch Panels"
	"changenote"	"$(Vdf $ChangeNote)"
	"tags"
	{
		"0"	"modded"
		"1"	"melonloader"
		"2"	"mod"
	}
}
"@
[System.IO.File]::WriteAllText((Join-Path $publish "workshop_item.vdf"), $vdf, $noBom)

@"
@echo off
echo Uploading Next-Gen Patch Panels v$version to the Steam Workshop.
echo SteamCMD will ask for your password and, if you use it, a Steam Guard code.
echo.
set /p STEAMUSER=Steam account name (the name you log in with, not your profile name):
"$SteamCmd" +login %STEAMUSER% +workshop_build_item "$publish\workshop_item.vdf" +quit
echo.
echo If it says "Success", the upload worked. Note the item ID it printed.
pause
"@ | Set-Content -Encoding ASCII (Join-Path $publish "upload.cmd")

Write-Host ""
Write-Host "Package for v$version ready in $publish (Workshop item ID: $id)."
