# Installs the Linux x64 vsdbg that belongs to the installed VS Code C# extension into
# %USERPROFILE%\vsdbg\vscode, which docker-compose.debug.yml mounts into the containers.
# The container's vsdbg must be the same release as the extension's own debugger
# (vsdbg-ui); an older one rejects every breakpoint with "Incorrect breakpoint request
# format". The public "latest" vsdbg lags behind the extension, so the package URL and
# SHA-256 are taken from the extension's package.json. Visual Studio's copy in
# vsdbg\vs2017u5 is left alone. Offline, an already installed copy is kept.
#
# -CopyTo "a,b,c": additionally copies it to /tmp/vsdbg in those of the containers that
# are running, for the "Attach ..." configurations against a stack started without the
# debug overlay (so docker-compose.dev.yml, which Visual Studio uses, needs no debugger
# mount). Comma-separated string, because `powershell -File` cannot pass arrays.
param([string]$CopyTo = '')
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:USERPROFILE 'vsdbg\vscode'
$marker = Join-Path $target 'kaimo-source-url.txt'

function Install-KaimoVsdbg($debugger) {
    if ((Test-Path $marker) -and (Get-Content $marker -Raw).Trim() -eq $debugger.url) { return }

    $zip = Join-Path $env:TEMP 'kaimo-coreclr-debug-linux-x64.zip'
    try {
        Invoke-WebRequest $debugger.url -UseBasicParsing -OutFile $zip
    } catch {
        if (Test-Path (Join-Path $target 'vsdbg')) {
            Write-Warning "vsdbg download failed, keeping the installed copy: $_"
            return
        }
        throw
    }
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $debugger.integrity) {
        Remove-Item $zip
        throw 'vsdbg package checksum does not match the C# extension.'
    }

    # Containers still using the old copy must stop first: Docker Desktop keeps the old
    # memory-mapped pages of a replaced file, and the new vsdbg then fails to load
    # ("unsupported version ... of Verdef record"). The following `up -d` restarts them.
    $running = docker ps -q --filter volume=/remote_debugger
    if ($running) { docker stop $running | Out-Null }

    # Keep the folder itself: it is the bind-mount source.
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem $target | Remove-Item -Recurse -Force
    Expand-Archive $zip -DestinationPath $target
    Remove-Item $zip
    Set-Content $marker $debugger.url -Encoding ascii
    Write-Output 'Installed vsdbg.'
}

function Copy-KaimoVsdbg($container, $url) {
    if (-not (docker ps -q --filter "name=^$container$")) { return }
    # Copied once per container and vsdbg release; a recreated container gets it again.
    $installed = docker exec $container sh -c 'cat /tmp/vsdbg/kaimo-source-url.txt 2>/dev/null'
    if ("$installed".Trim() -eq $url) { return }
    docker exec -u 0 $container rm -rf /tmp/vsdbg
    docker cp $target "${container}:/tmp/vsdbg"
    if ($LASTEXITCODE -ne 0) { throw "Copying vsdbg into $container failed." }
    Write-Output "Copied vsdbg into $container."
}

$extension = Get-ChildItem (Join-Path $env:USERPROFILE '.vscode\extensions') -Directory -Filter 'ms-dotnettools.csharp-*' |
    Sort-Object { [version]($_.Name -replace '^ms-dotnettools\.csharp-([\d.]+).*$', '$1') } -Descending |
    Select-Object -First 1
if (-not $extension) { throw 'VS Code C# extension (ms-dotnettools.csharp) not found.' }

$package = Get-Content (Join-Path $extension.FullName 'package.json') -Raw | ConvertFrom-Json
$debugger = $package.runtimeDependencies |
    Where-Object { $_.id -eq 'Debugger' -and $_.platforms -contains 'linux' -and $_.architectures -contains 'x86_64' } |
    Select-Object -First 1
if (-not $debugger) { throw "No Linux x64 debugger package listed in $($extension.Name)." }

Install-KaimoVsdbg $debugger
$installedUrl = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }
foreach ($container in ($CopyTo -split ',' | Where-Object { $_ })) { Copy-KaimoVsdbg $container.Trim() $installedUrl }
Write-Output "vsdbg for $($extension.Name) is ready."
