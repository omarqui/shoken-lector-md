using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core;
using LectorMd.Core.Model;
using LectorMd.Core.Planning;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;

namespace LectorMd.App;

/// <summary>
/// Editar la pronunciación de lo seleccionado en el documento: al seleccionar 1 a 3 palabras aparece un botón
/// junto a la selección; al pulsarlo, un editor para agregarla al diccionario, cambiarla, quitarla o escucharla.
/// </summary>
public partial class MainWindow
{
    private readonly MediaPlayer _previewPlayer = new();
    private string? _pronounceTerm;
    private string? _pronounceAnchor;

    private void OnSelectionMade(string text, Point at, string? anchor)
    {
        if (_services is null || PronounceEditor.Visibility == Visibility.Visible)
        {
            return; // con el editor abierto no se reemplaza lo que se está editando
        }

        _pronounceTerm = text;
        _pronounceAnchor = anchor;
        string? current = CurrentPronunciation(text);
        PronounceChipText.Text = current is null ? "Pronunciación" : $"«{current}» · editar";
        System.Windows.Automation.AutomationProperties.SetName(PronounceChip,
            current is null ? $"Agregar pronunciación de {text}" : $"Editar pronunciación de {text}");
        PronounceChip.Visibility = Visibility.Visible;
        PronounceEditor.Visibility = Visibility.Collapsed;
        PronouncePopup.HorizontalOffset = at.X;
        PronouncePopup.VerticalOffset = at.Y + 4;
        PronouncePopup.IsOpen = true;
    }

    private void OnSelectionCleared()
    {
        if (PronounceEditor.Visibility != Visibility.Visible)
        {
            PronouncePopup.IsOpen = false;
        }
    }

    private string? CurrentPronunciation(string term) =>
        _services?.Normalizer.Dictionary.Entries.TryGetValue(term.Trim(), out string? spoken) == true ? spoken : null;

    private void OnPronounceChipClick(object sender, RoutedEventArgs e)
    {
        if (_pronounceTerm is not { } term)
        {
            return;
        }

        string? current = CurrentPronunciation(term);
        PronounceTitle.Text = $"Pronunciación de «{term}»";
        PronounceBox.Text = current ?? term;
        PronounceHint.Text = current is null
            ? "No está en el diccionario: se agrega al guardar. Escríbelo como se dice en español."
            : "Está en el diccionario. Los cambios se aplican a todos los documentos.";
        PronounceRemove.IsEnabled = current is not null;
        PronounceChip.Visibility = Visibility.Collapsed;
        PronounceEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            // El popup no se activa (WS_EX_NOACTIVATE): sus teclas le llegan por la ventana principal. Si el foco
            // de Windows sigue en el WebView2 (se acaba de seleccionar en el documento), las teclas irían a los
            // atajos de lector.js aunque WPF crea que el cuadro tiene el foco. Enfocar la ventana se lo devuelve.
            Focus();
            PronounceBox.Focus();
            PronounceBox.SelectAll();
        });
    }

    private void OnPronounceSaveClick(object sender, RoutedEventArgs e)
    {
        if (_pronounceTerm is { } term && !string.IsNullOrWhiteSpace(PronounceBox.Text))
        {
            SavePronunciation(term, PronounceBox.Text.Trim());
        }
    }

    private void OnPronounceRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_pronounceTerm is { } term)
        {
            SavePronunciation(term, null);
        }
    }

    private void OnPronounceCancelClick(object sender, RoutedEventArgs e) => ClosePronounceEditor();

    private void OnPronounceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClosePronounceEditor();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnPronounceSaveClick(sender, e);
        }
    }

    /// <summary>Cierra el editor y devuelve el teclado al documento (desplazamiento y atajos).</summary>
    private void ClosePronounceEditor()
    {
        PronouncePopup.IsOpen = false;
        Browser.Focus();
    }

    private void OnPronouncePopupClosed(object? sender, EventArgs e)
    {
        PronounceEditor.Visibility = Visibility.Collapsed;
        PronounceChip.Visibility = Visibility.Visible;
    }

    /// <summary>Guarda en el diccionario (o quita, con null) y recarga la pestaña en el mismo párrafo.</summary>
    private void SavePronunciation(string term, string? spoken)
    {
        try
        {
            PronunciationDictionary.Upsert(_paths.PronunciationFile, term, spoken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorLog.Write(ex);
            PronounceHint.Text = $"No se pudo guardar el diccionario: {ex.Message}";
            return;
        }

        ClosePronounceEditor();
        int resume = _player.CurrentSegmentId;
        RebuildServices(); // vuelve a leer el diccionario
        if (_active is { State.IsEmpty: false })
        {
            _ = ActivateAsync(_active, force: true, resumeSegmentId: resume >= 0 ? resume : null);
        }

        ShowStatus(spoken is null
            ? $"«{term}» ya no está en el diccionario."
            : $"«{term}» se leerá como «{spoken}». Los bloques que lo contienen se vuelven a generar al escucharlos.");
    }

    /// <summary>
    /// Lee el párrafo de la selección con la pronunciación escrita, sin guardarla. Usa el motor elegido;
    /// con Azure o Google gasta los caracteres de ese párrafo.
    /// </summary>
    private async void OnPronounceListenClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _pronounceTerm is not { } term || string.IsNullOrWhiteSpace(PronounceBox.Text))
        {
            return;
        }

        string text = _pronounceAnchor is not null && _document?.FirstForAnchor(_pronounceAnchor) is { } segment
            ? segment.PlainText
            : term;
        PronunciationDictionary preview = _services.Normalizer.Dictionary.WithEntry(term, PronounceBox.Text.Trim());

        if (IsReading)
        {
            _player.Pause(); // que no suenen las dos cosas a la vez
        }

        PronounceListen.IsEnabled = false;
        PronounceHint.Text = "Generando…";
        try
        {
            using EngineServices services = EngineServices.Create(_settings, _paths, preview);
            SpeechDocument document = services.Parse(text, null);
            if (services.Plan(document).FirstOrDefault() is not { } chunk)
            {
                return;
            }

            AudioChunk audio = await Task.Run(() => services.Engine.SynthesizeAsync(chunk, CancellationToken.None));
            _previewPlayer.Open(new Uri(audio.FilePath, UriKind.Absolute));
            _previewPlayer.SpeedRatio = _settings.PlaybackSpeed;
            _previewPlayer.Play();
            PronounceHint.Text = audio.FromCache ? "Así suena (audio de la caché)." : $"Así suena con {Provider(audio.EngineName)}.";
        }
        catch (TtsException ex)
        {
            PronounceHint.Text = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            ErrorLog.Write(ex);
            PronounceHint.Text = $"No se pudo escuchar: {ex.Message}";
        }
        finally
        {
            PronounceListen.IsEnabled = true;
            UpdateUsage();
        }
    }
}
