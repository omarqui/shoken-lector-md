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
}
finally {
    $word.Quit()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
}
