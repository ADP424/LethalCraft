# LethalCraft developer install: builds the plugin and copies it into a BepInEx plugins folder.
#
#   tools\install-dev.ps1                          build + install into the r2modman profile "LethalCraftDev"
#   tools\install-dev.ps1 -Profile "MinumGear"     another r2modman profile
#   tools\install-dev.ps1 -SkipFabric              only the Lethal Company plugin
#   tools\install-dev.ps1 -SkipPlugin              only the Minecraft mod
#
# Game side: needs a r2modman profile that already has BepInEx (create one in r2modman, install nothing
# else). Minecraft side: a "LethalCraft" instance in Prism Launcher (Minecraft + Fabric Loader, see
# fabric/gradle.properties) with Fabric API, e4mc and the LethalCraft mod.
param(
	[string]$GameDir = "D:\Steam\steamapps\common\Lethal Company",
	[string]$Profile = "LethalCraftDev",
	[string]$PrismData = "$env:APPDATA\PrismLauncher",
	[switch]$SkipFabric,
	[switch]$SkipPlugin
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root ".tools"

if (-not $SkipPlugin) {
	$managed = Join-Path $GameDir "Lethal Company_Data\Managed"
	$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\LethalCompany\profiles\$Profile\BepInEx"
	if (-not (Test-Path (Join-Path $profileDir "core\BepInEx.dll"))) {
		throw "No BepInEx in r2modman profile '$Profile' ($profileDir). Create the profile in r2modman first."
	}
	# The plugin compiles against a publicized copy of the game's Assembly-CSharp (its internals).
	$publicized = Join-Path $tools "publicized\Assembly-CSharp.dll"
	$original = Join-Path $managed "Assembly-CSharp.dll"
	if (-not (Test-Path $publicized) -or (Get-Item $publicized).LastWriteTime -lt (Get-Item $original).LastWriteTime) {
		Write-Host "Publicizing Assembly-CSharp"
		New-Item -ItemType Directory -Force (Split-Path $publicized) | Out-Null
		dotnet build (Join-Path $root "tools\Publicizer\Publicizer.csproj") -c Release -nologo -v q
		dotnet (Join-Path $root "tools\Publicizer\bin\Release\net8.0\Publicizer.dll") $original $publicized $managed
		if ($LASTEXITCODE -ne 0) { throw "publicizing failed" }
	}
	Write-Host "Building the Lethal Company plugin"
	dotnet build (Join-Path $root "plugin\LethalCraft.csproj") -c Release -nologo -v q -p:GameDir="$GameDir" -p:BepInExCore="$(Join-Path $profileDir 'core')"
	if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
	$plugins = Join-Path $profileDir "plugins\LethalCraft"
	New-Item -ItemType Directory -Force $plugins | Out-Null
	Copy-Item -Force (Join-Path $root "plugin\bin\Release\netstandard2.1\LethalCraft.dll") $plugins
	Copy-Item -Force (Join-Path $root "plugin\bin\Release\netstandard2.1\LethalCraft.pdb") $plugins -ErrorAction SilentlyContinue
	Write-Host "  -> $plugins"
}

if (-not $SkipFabric) {
	Write-Host "Building the Minecraft mod"
	Push-Location (Join-Path $root "fabric")
	try {
		# Gradle writes compiler notes to stderr: only its exit code says whether it failed.
		$ErrorActionPreference = "Continue"
		& .\gradlew.bat --no-daemon -q build -x test 2>&1 | ForEach-Object { "$_" }
		$ErrorActionPreference = "Stop"
		if ($LASTEXITCODE -ne 0) { throw "fabric build failed" }
	} finally {
		Pop-Location
	}
	$version = (Select-String -Path (Join-Path $root "fabric\gradle.properties") -Pattern '^version=(.*)$').Matches[0].Groups[1].Value
	$jar = Join-Path $root "fabric\build\libs\lethalcraft-$version.jar"
	# Prism keeps an instance's game folder as "minecraft" (older MultiMC-style instances: ".minecraft").
	$instance = Join-Path $PrismData "instances\LethalCraft"
	$gameFolder = "minecraft"
	if ((Test-Path (Join-Path $instance ".minecraft") -PathType Container) -and -not (Test-Path (Join-Path $instance "minecraft"))) { $gameFolder = ".minecraft" }
	$mods = Join-Path $instance "$gameFolder\mods"
	New-Item -ItemType Directory -Force $mods | Out-Null
	Get-ChildItem $mods -Filter "lethalcraft-*.jar" | Remove-Item -Force
	Copy-Item -Force $jar $mods
	Write-Host "  -> $mods (Fabric API and e4mc go here too: see fabric/build.gradle for versions)"
}
Write-Host "Done."
