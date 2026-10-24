<#
.SYNOPSIS
    Arma el paquete MSIX de Shoken para la Microsoft Store.

.DESCRIPTION
    Publica la app autocontenida (win-x64, con el runtime de .NET dentro), genera los logos desde Assets\app.ico,
    compila resources.pri y empaqueta con makeappx. El .msix queda en publicar-msix\ sin firmar: la Store lo firma.

    La versión sale de Directory.Build.props (tres números; el cuarto lo reserva la Store y va en 0). La identidad del
    paquete está en empaquetado\AppxManifest.xml. makeappx y makepri se descargan una vez del paquete NuGet
    Microsoft.Windows.SDK.BuildTools a obj\empaquetado\, así que no hace falta instalar el Windows SDK.

.PARAMETER Registrar
    Además instala la app en este equipo desde la carpeta del paquete, para probarla. Requiere el modo de
    desarrollador (Configuración > Sistema > Para programadores). Volver a correr el script la actualiza.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File empaquetado\empaquetar.ps1 -Registrar
#>
param([switch]$Registrar)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # la barra de progreso hace muy lenta la descarga en Windows PowerShell

$raiz = Split-Path $PSScriptRoot -Parent
$trabajo = Join-Path $raiz 'obj\empaquetado'
$contenido = Join-Path $trabajo 'contenido'
$pri = Join-Path $trabajo 'pri'
$salida = Join-Path $raiz 'publicar-msix'

# --- Versión ---------------------------------------------------------------------------------------------------------

$version = (Select-Xml -Path (Join-Path $raiz 'Directory.Build.props') -XPath '//Version').Node.InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La versión de Directory.Build.props debe tener tres números (por ejemplo 0.1.0), no '$version'."
}
$version = "$version.0"

# --- makeappx y makepri ----------------------------------------------------------------------------------------------

$herramientasVersion = '10.0.28000.2705'
$herramientas = Join-Path $trabajo "buildtools-$herramientasVersion"
$bin = Join-Path $herramientas ('bin\' + (($herramientasVersion -split '\.')[0..2] -join '.') + '.0\x64')
if (-not (Test-Path (Join-Path $bin 'makeappx.exe'))) {
    Write-Host "Descargando Microsoft.Windows.SDK.BuildTools $herramientasVersion (makeappx y makepri)..."
    New-Item -ItemType Directory -Force $trabajo | Out-Null
    $zip = Join-Path $trabajo 'buildtools.zip'
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$herramientasVersion/" +
        "microsoft.windows.sdk.buildtools.$herramientasVersion.nupkg"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $herramientas -Force
    Remove-Item $zip
}
$makeappx = Join-Path $bin 'makeappx.exe'
$makepri = Join-Path $bin 'makepri.exe'

function Invoke-Herramienta([string]$exe, [string[]]$argumentos) {
    # Su salida es larga: solo se muestra si falla.
    $log = & $exe @argumentos
    if ($LASTEXITCODE -ne 0) {
        $log | Write-Host
        throw "Falló $(Split-Path $exe -Leaf)."
    }
}

# --- Publicar --------------------------------------------------------------------------------------------------------

foreach ($carpeta in $contenido, $pri) {
    if (Test-Path $carpeta) {
        Remove-Item $carpeta -Recurse -Force
    }
}

Write-Host "Publicando Shoken $version (win-x64, autocontenida)..."
dotnet publish (Join-Path $raiz 'src\Shoken.App') -c Release -r win-x64 --self-contained true -p:DebugType=none -o $contenido
if ($LASTEXITCODE -ne 0) {
    throw 'Falló dotnet publish.'
}

$manifiesto = [xml](Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding UTF8)
$manifiesto.Package.Identity.Version = $version
$manifiesto.Save((Join-Path $contenido 'AppxManifest.xml'))

# --- Logos -----------------------------------------------------------------------------------------------------------

Add-Type -AssemblyName System.Drawing

function Read-IconPng([string]$ruta) {
    # El cuadro de 256 px de un .ico va guardado como PNG: se toma tal cual, con su transparencia.
    $bytes = [IO.File]::ReadAllBytes($ruta)
    $cuadros = [BitConverter]::ToUInt16($bytes, 4)
    for ($i = 0; $i -lt $cuadros; $i++) {
        $entrada = 6 + 16 * $i
        if ($bytes[$entrada] -eq 0) { # ancho 0 = 256 px
            $tamano = [BitConverter]::ToInt32($bytes, $entrada + 8)
            $inicio = [BitConverter]::ToInt32($bytes, $entrada + 12)
            return [Drawing.Image]::FromStream([IO.MemoryStream]::new($bytes, $inicio, $tamano))
        }
    }
    throw "$ruta no tiene un cuadro de 256 px."
}

function Save-Logo([Drawing.Image]$icono, [int]$ancho, [int]$alto, [double]$proporcion, [string]$nombre) {
    # Fondo transparente y el ícono centrado; $proporcion es su lado respecto del alto del lienzo.
    $lienzo = [Drawing.Bitmap]::new($ancho, $alto)
    $g = [Drawing.Graphics]::FromImage($lienzo)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $lado = [int][Math]::Round($alto * $proporcion)
    $g.DrawImage($icono, [int][Math]::Floor(($ancho - $lado) / 2), [int][Math]::Floor(($alto - $lado) / 2), $lado, $lado)
    $g.Dispose()
    $lienzo.Save((Join-Path $imagenes $nombre), [Drawing.Imaging.ImageFormat]::Png)
    $lienzo.Dispose()
}

$imagenes = Join-Path $pri 'Images'
New-Item -ItemType Directory -Force $imagenes | Out-Null
$icono = Read-IconPng (Join-Path $raiz 'src\Shoken.App\Assets\app.ico')
foreach ($lado in 16, 24, 32, 48, 256) {
    # Barra de tareas, menú Inicio y Explorador; "unplated" evita que Windows le ponga un fondo de color.
    Save-Logo $icono $lado $lado 1 "Square44x44Logo.targetsize-$lado.png"
    Save-Logo $icono $lado $lado 1 "Square44x44Logo.targetsize-${lado}_altform-unplated.png"
}
foreach ($escala in 100, 200) {
    $f = $escala / 100
    Save-Logo $icono (44 * $f) (44 * $f) 1 "Square44x44Logo.scale-$escala.png"
    Save-Logo $icono (150 * $f) (150 * $f) 0.66 "Square150x150Logo.scale-$escala.png"
    Save-Logo $icono (310 * $f) (150 * $f) 0.66 "Wide310x150Logo.scale-$escala.png"
    Save-Logo $icono (50 * $f) (50 * $f) 1 "StoreLogo.scale-$escala.png"
}
$icono.Dispose()

# resources.pri elige el tamaño de cada logo. Se indexa aparte para no meter las DLL de la app como recursos.
$priconfig = Join-Path $trabajo 'priconfig.xml'
Invoke-Herramienta $makepri @('createconfig', '/cf', $priconfig, '/dq', 'es-MX', '/pv', '10.0.0', '/o')
Invoke-Herramienta $makepri @('new', '/pr', $pri, '/cf', $priconfig, '/mn', (Join-Path $contenido 'AppxManifest.xml'),
    '/of', (Join-Path $contenido 'resources.pri'), '/o')
Copy-Item $imagenes (Join-Path $contenido 'Images') -Recurse

# --- Empaquetar ------------------------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force $salida | Out-Null
$msix = Join-Path $salida "Shoken_${version}_x64.msix"
Invoke-Herramienta $makeappx @('pack', '/d', $contenido, '/p', $msix, '/o')
Write-Host "Listo: $msix"

if ($Registrar) {
    # Instala desde la carpeta (sin firma): solo con el modo de desarrollador activado.
    Add-AppxPackage -Register (Join-Path $contenido 'AppxManifest.xml') -ForceApplicationShutdown
    Write-Host 'Shoken quedó instalada en este equipo (búscala en el menú Inicio).'
}
