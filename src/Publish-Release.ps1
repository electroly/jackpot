$ErrorPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Path $PSScriptRoot -Parent

$publishDir = "$root\publish"
if (Test-Path $publishDir) {
	[System.IO.Directory]::Delete($publishDir, $true) | Out-Null
}

$buildDir = "$root\publish\build"
[System.IO.Directory]::CreateDirectory($buildDir) | Out-Null

$downloadsDir = "$root\downloads"
[System.IO.Directory]::CreateDirectory($downloadsDir) | Out-Null

# Windows SDK
$windowsSdkBaseDir = "C:\Program Files (x86)\Windows Kits\10\Redist"
$windowsSdkVersion = `
    Get-ChildItem -Path $windowsSdkBaseDir | 
    Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } | 
    Sort-Object Name -Descending | 
    Select-Object -First 1 -ExpandProperty Name

$makeappx = "C:\Program Files (x86)\Windows Kits\10\bin\$windowsSdkVersion\x64\makeappx.exe"
if (Test-Path $makeappx) {
    Write-Output "MakeAppx: $makeappx"
} else {
	throw "MakeAppx not found!"
}

$makepri = "C:\Program Files (x86)\Windows Kits\10\bin\$windowsSdkVersion\x64\makepri.exe"
if (Test-Path $makepri) {
    Write-Output "MakePri: $makepri"
} else {
	throw "MakePri not found!"
}

function Publish-App
{
	param
	(
		[Parameter(Mandatory = $true)] [string] $Arch
	)

	Write-Host "Publishing $Arch."
	$dir = "$buildDir\$Arch"
	[System.IO.Directory]::CreateDirectory($dir) | Out-Null
	dotnet publish "$root/src/J.App/J.App.csproj" --output "$dir" --self-contained --runtime "win-$Arch" --configuration Release --verbosity quiet
	Remove-Item -Path "$dir\*.pdb" -Force
}

function Publish-Launcher
{
	dotnet publish "$root/src/J.Launcher/J.Launcher.csproj" --output "$buildDir" --self-contained --runtime "win-x64" --configuration Release --verbosity quiet
	Remove-Item -Path "$buildDir\*.pdb" -Force
}

function Get-FfmpegX64
{
	$url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
	$zipFilePath = "$downloadsDir\ffmpeg-x64.zip"
	if (-not (Test-Path $zipFilePath))
	{
		Write-Host "Downloading ffmpeg/x64."
		& curl.exe -Lso "$zipFilePath" "$url" | Out-Host
		if ($LastExitCode -ne 0) {
			throw "Failed to download ffmpeg/x64."
		}
	}

	Write-Host "Extracting ffmpeg/x64."
	$dstDir = "$buildDir\x64\ffmpeg\"
	[System.IO.Directory]::CreateDirectory($dstDir) | Out-Null
	[System.IO.Compression.ZipFile]::ExtractToDirectory($zipFilePath, $dstDir)

	# Get the only subdirectory of $dstDir.
	$ffmpegDir = Get-ChildItem -Path $dstDir -Directory | Select-Object -First 1

	# Move everything in $ffmpegDir\bin\ to $dstDir.
	$ffmpegBinDir = "$($ffmpegDir.FullName)\bin"
	Move-Item -Path "$ffmpegBinDir\*" -Destination $dstDir -Force

	Remove-Item -Path $ffmpegDir.FullName -Recurse -Force
}

function Get-FfmpegArm64
{
	$url = "https://github.com/dvhh/ffmpeg-wos-arm64-build/releases/download/main/ffmpeg-wos-arm64.zip"
	$zipFilePath = "$downloadsDir\ffmpeg-arm64.zip"
	if (-not (Test-Path $zipFilePath))
	{
		Write-Host "Downloading ffmpeg/arm64."
		& curl.exe -Lso "$zipFilePath" "$url" | Out-Host
		if ($LastExitCode -ne 0) {
			throw "Failed to download ffmpeg/arm64."
		}
	}

	Write-Host "Extracting ffmpeg/arm64."
	$dstDir = "$buildDir\arm64\ffmpeg\"
	[System.IO.Directory]::CreateDirectory($dstDir) | Out-Null
	[System.IO.Compression.ZipFile]::ExtractToDirectory($zipFilePath, $dstDir)
}

function Get-Vlc
{
	$url = "https://get.videolan.org/vlc/last/win64/"
	$htmlFilePath = "$downloadsDir\vlc.html"
	if (-not (Test-Path $htmlFilePath))
	{
		Write-Host "Finding current release of vlc/x64."
		& curl.exe -Lso "$htmlFilePath" "$url" | Out-Host
		if ($LastExitCode -ne 0) {
			throw "Failed to download vlc.html."
		}
	}

	# Read the html file, find the first line that contains ".zip"
	$line = Select-String -Path $htmlFilePath -Pattern "\.zip" | Select-Object -First 1

	# $line is like: <a href="vlc-3.0.21-win64.zip">vlc-3.0.21-win64.zip</a>
	# Parse out the .zip filename between the quotes.
	$zipFilename = $line -replace '.*href="([^"]+)".*', '$1'

	$url = "https://get.videolan.org/vlc/last/win64/$zipFilename"
	$zipFilePath = "$downloadsDir\vlc.zip"
	if (-not (Test-Path $zipFilePath))
	{
		Write-Host "Downloading vlc/x64."
		& curl.exe -Lso "$zipFilePath" "$url" | Out-Host
		if ($LastExitCode -ne 0) {
			throw "Failed to download vlc/x64."
		}
	}

	Write-Host "Extracting vlc/x64."
	$dstDir = "$buildDir\vlc\"
	[System.IO.Directory]::CreateDirectory($dstDir) | Out-Null
	[System.IO.Compression.ZipFile]::ExtractToDirectory($zipFilePath, $dstDir)

	# Get the only subdirectory of $dstDir.
	$subdir = Get-ChildItem -Path $dstDir -Directory | Select-Object -First 1

	# Confirm $subdir is the directory with vlc.exe in it.
	if (-not (Test-Path "$($subdir.FullName)\vlc.exe")) {
		throw "Failed to find vlc.exe in extracted directory."
	}

	# Move everything in $subdir to $dstDir.
	Move-Item -Path "$($subdir.FullName)\*" -Destination $dstDir -Force

	# Delete $subdir.
	Remove-Item -Path $subdir.FullName -Recurse -Force

	# Generate plugins\plugins.dat by running vlc-cache-gen.
	if (Test-Path "$dstDir\plugins\plugins.dat") {
		throw "plugins.dat already exists."
	}

	Push-Location $dstDir
	try
	{
		& .\vlc-cache-gen.exe .\plugins | Out-Host
	}
	finally
	{
		Pop-Location
	}

	if (-not (Test-Path "$dstDir\plugins\plugins.dat")) {
		throw "Failed to generate plugins.dat."
	}
}

function Copy-MiscFiles
{
	Copy-Item -Path "$root\COPYING" -Destination "$buildDir\COPYING"
	Copy-Item -Path "$root\NOTICE" -Destination "$buildDir\NOTICE"
	Copy-Item -Path "$root\src\AppxManifest.xml" -Destination "$buildDir\AppxManifest.xml"

	[System.IO.Directory]::CreateDirectory("$buildDir\assets") | Out-Null
	Copy-Item -Path "$root\src\J.App\Resources\App.png" -Destination "$buildDir\assets\App.png"
	Copy-Item -Path "$root\src\J.App\Resources\App310x150.png" -Destination "$buildDir\assets\App310x150.png"
	Copy-Item -Path "$root\src\J.App\Resources\App150x150.png" -Destination "$buildDir\assets\App150x150.png"
	Copy-Item -Path "$root\src\J.App\Resources\App44x44.png" -Destination "$buildDir\assets\App44x44.png"
	Copy-Item -Path "$root\src\J.App\Resources\App44x44.png" -Destination "$buildDir\assets\App44x44.targetsize-44_altform-unplated.png"

	foreach ($x in 16, 24, 32, 48, 256)
	{
		Copy-Item -Path "$root\src\J.App\Resources\App${x}x${x}.png" -Destination "$buildDir\assets\App44x44.targetsize-${x}.png"
		Copy-Item -Path "$root\src\J.App\Resources\App${x}x${x}.png" -Destination "$buildDir\assets\App44x44.altform-unplated_targetsize-${x}.png"
	}

	Push-Location $buildDir
	try
	{
		Write-Host "`n--- Start: MakePri createconfig ---"
		& "$makepri" createconfig /cf "priconfig.xml" /dq en-US | Out-Host
		if ($LastExitCode -ne 0) {
			throw "MakePri createconfig failed."
		}
		Write-Host "--- End: MakePri createconfig ---`n"

		Write-Host "--- Start: MakePri new ---"
		& "$makepri" new /pr "$buildDir" /cf "priconfig.xml" | Out-Host
		if ($LastExitCode -ne 0) {
			throw "MakePri new failed."
		}
		Write-Host "--- End: MakePri new ---`n"
	}
	finally
	{
		Pop-Location
	}
}

function New-Msix
{
	Write-Host "Creating MSIX package."
	$msixFilePath = "$root\publish\Jackpot.msix"
	if (Test-Path $msixFilePath) { Remove-Item -Path $msixFilePath -Force }
	Write-Host "`n--- Start: MakeAppx pack ---"
	& "$makeappx" pack /d "$buildDir" /p "$msixFilePath"
	if ($LastExitCode -ne 0) {
		throw "Failed to create MSIX package."
	}
	Write-Host "--- End: MakeAppx pack ---`n"
}

Copy-MiscFiles
Publish-Launcher
Publish-App -Arch "x64"
Publish-App -Arch "arm64"
Get-FfmpegX64
Get-FfmpegArm64
Get-Vlc
New-Msix
