# Genera los .docx de muestra de WordConverterTests con Word (por COM, invisible). Necesita Word instalado; los .docx
# resultantes se versionan, así que las pruebas no lo necesitan. Guarda este archivo con BOM: PowerShell 5.1 lee como
# ANSI un .ps1 sin BOM y los acentos llegarían rotos al Word.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Shoken.Core.Tests\Fixtures\Word\crear-muestras.ps1
param([string]$Salida = $PSScriptRoot)

# wdBuiltinStyle (no dependen del idioma de Word)
$Titulo = -63; $H1 = -2; $H2 = -3; $Normal = -1; $Lista = -49; $ListaNum = -50; $Cita = -181

$word = New-Object -ComObject Word.Application
$word.Visible = $false
$word.DisplayAlerts = 0

function Nuevo { $word.Documents.Add() }

function P($sel, [string]$texto, [int]$estilo) {
    $sel.Style = $estilo
    $sel.TypeText($texto)
    $sel.TypeParagraph()
}

function Guardar($doc, [string]$nombre) {
    [string]$ruta = Join-Path $Salida $nombre
    $doc.SaveAs2($ruta, 16) # wdFormatDocumentDefault
    $doc.Close([ref]0)
    "creado: $nombre"
}

try {
    # ------------------------------------------------------------------ estructura.docx
    $doc = Nuevo; $sel = $word.Selection
    P $sel 'Guía de uso del lector' $Titulo
    P $sel 'Introducción' $H1
    $sel.Style = $Normal
    $sel.TypeText('Este documento prueba la conversión. Tiene texto en ')
    $sel.Font.Bold = 1; $sel.TypeText('negrita'); $sel.Font.Bold = 0
    $sel.TypeText(', en ')
    $sel.Font.Italic = 1; $sel.TypeText('cursiva'); $sel.Font.Italic = 0
    $sel.TypeText(' y una nota al pie. Sigue el párrafo.')
    $sel.TypeParagraph()
    P $sel 'Requisitos' $H2
    P $sel 'El SDK de .NET 10.' $Lista
    P $sel 'El runtime de WebView2.' $Lista
    P $sel 'Pasos' $H2
    P $sel 'Abre el documento.' $ListaNum
    P $sel 'Presiona Reproducir.' $ListaNum
    P $sel 'Motores de voz' $H1
    P $sel 'La tabla resume los motores:' $Normal
    $t = $doc.Tables.Add($sel.Range, 3, 2)
    $t.Borders.Enable = 1
    $datos = @(@('Motor', 'Costo'), @('Azure', 'Cuota mensual'), @('Piper', 'Gratis'))
    for ($r = 0; $r -lt 3; $r++) { for ($c = 0; $c -lt 2; $c++) { $t.Cell($r + 1, $c + 1).Range.Text = $datos[$r][$c] } }
    $t.Rows.Item(1).Range.Font.Bold = 1
    $sel.EndKey(6) | Out-Null # wdStory: salir de la tabla
    $sel.TypeParagraph()
    P $sel 'Leer en voz alta cambia cómo se entiende un texto.' $Cita
    P $sel 'Conclusión' $H1
    P $sel 'Con esto termina la prueba.' $Normal
    # La nota al final, sobre el texto ya escrito: con la selección, Word la deja fuera de su párrafo.
    $rango = $doc.Content
    if ($rango.Find.Execute('una nota al pie')) { $rango.Collapse(0); $doc.Footnotes.Add($rango, [Type]::Missing, 'Esta es la nota al pie.') | Out-Null }
    $sec = $doc.Sections.Item(1)
    $sec.Headers.Item(1).Range.Text = 'Shoken - documento de prueba'
    $sec.Footers.Item(1).PageNumbers.Add() | Out-Null
    Guardar $doc 'estructura.docx'

    # ------------------------------------------------------------------ especiales-texto.docx
    $doc = Nuevo; $sel = $word.Selection
    P $sel 'Ver el punto [1] del contrato.' $Normal
    P $sel 'Esta palabra BORRADO se quitó con control de cambios.' $Normal
    P $sel 'Este párrafo tiene un comentario.' $Normal
    $doc.Comments.Add($doc.Paragraphs.Item(3).Range, 'COMENTARIO') | Out-Null
    $doc.TrackRevisions = $true
    $rango = $doc.Paragraphs.Item(2).Range
    $hallado = $rango.Find.Execute('BORRADO ')
    if ($hallado) { $rango.Delete() | Out-Null }
    $doc.TrackRevisions = $false
    Guardar $doc 'especiales-texto.docx'

    # ------------------------------------------------------------------ imágenes (PNG, JPEG y EMF de prueba)
    Add-Type -AssemblyName System.Drawing
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('shoken-muestras-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    function Mapa([string]$ruta, $color, $formato) {
        $bmp = New-Object System.Drawing.Bitmap 120, 60
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear($color); $g.Dispose()
        $bmp.Save($ruta, $formato); $bmp.Dispose()
    }
    [string]$png = Join-Path $tmp 'azul.png'; Mapa $png ([System.Drawing.Color]::SteelBlue) ([System.Drawing.Imaging.ImageFormat]::Png)
    [string]$jpg = Join-Path $tmp 'verde.jpg'; Mapa $jpg ([System.Drawing.Color]::SeaGreen) ([System.Drawing.Imaging.ImageFormat]::Jpeg)
    [string]$emf = Join-Path $tmp 'dibujo.emf'
    $ref = New-Object System.Drawing.Bitmap 1, 1; $g0 = [System.Drawing.Graphics]::FromImage($ref); $hdc = $g0.GetHdc()
    $mf = New-Object System.Drawing.Imaging.Metafile($emf, $hdc); $g0.ReleaseHdc($hdc); $g0.Dispose(); $ref.Dispose()
    $g = [System.Drawing.Graphics]::FromImage($mf); $g.FillRectangle([System.Drawing.Brushes]::Tomato, 0, 0, 100, 50); $g.Dispose(); $mf.Dispose()

    # ------------------------------------------------------------------ imagenes.docx
    $doc = Nuevo; $sel = $word.Selection
    P $sel 'Primera imagen:' $Normal
    $img = $sel.InlineShapes.AddPicture($png); $img.AlternativeText = 'Logo de Shoken'; $sel.TypeParagraph()
    P $sel 'La misma, sin texto alternativo:' $Normal
    $sel.InlineShapes.AddPicture($png) | Out-Null; $sel.TypeParagraph()
    P $sel 'Una foto:' $Normal
    $img = $sel.InlineShapes.AddPicture($jpg); $img.AlternativeText = "Paso [2] *importante*`ncon salto"; $sel.TypeParagraph()
    P $sel 'Fin.' $Normal
    Guardar $doc 'imagenes.docx'

    # ------------------------------------------------------------------ especiales.docx
    $doc = Nuevo; $sel = $word.Selection
    P $sel 'Antes del EMF.' $Normal
    $img = $sel.InlineShapes.AddPicture($emf); $img.AlternativeText = 'Diagrama EMF'; $sel.TypeParagraph()
    P $sel 'Entre el EMF y el gráfico.' $Normal
    $graf = $sel.InlineShapes.AddChart2(-1, 51); $graf.AlternativeText = 'Gráfico de ventas'
    $graf.Chart.ChartData.Workbook.Application.Quit()
    $sel.EndKey(6) | Out-Null; $sel.TypeParagraph()
    P $sel 'Entre el gráfico y el EMF sin texto.' $Normal
    $sel.InlineShapes.AddPicture($emf) | Out-Null; $sel.TypeParagraph()
    P $sel 'Después de todo.' $Normal
    Guardar $doc 'especiales.docx'

    Remove-Item -Recurse -Force $tmp
}
finally {
    $word.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
}
