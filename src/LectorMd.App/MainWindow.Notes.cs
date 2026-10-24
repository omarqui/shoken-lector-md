using System.IO;
using System.Windows;
using LectorMd.App.Services;
using LectorMd.Core;
using LectorMd.Core.Model;
using LectorMd.Core.Notes;

namespace LectorMd.App;

/// <summary>
/// Apuntes sobre el documento, como comentarios: la página los ubica y edita; aquí se guardan (uno por documento,
/// en %LOCALAPPDATA%\LectorMd\apuntes), se traducen a líneas del Markdown y se copian para pegarlos en una IA.
/// </summary>
public partial class MainWindow
{
    private NoteStore? _noteStore;
    private NotesContext? _notes;
    private DocumentNote? _lastDeletedNote; // para "Deshacer" en la página

    /// <summary>Los apuntes del documento de la pestaña activa, con lo que hace falta para ubicarlos.</summary>
    private sealed record NotesContext(DocumentTab Tab, SpeechDocument Document, IReadOnlyList<OutlineEntry> Outline, List<DocumentNote> Notes)
    {
        public string Path => Tab.State.Path;
    }

    private NoteStore Notes => _noteStore ??= new NoteStore(_paths.NotesDirectory);

    /// <summary>Al mostrar un documento: sus apuntes, antes de restaurar el desplazamiento (la columna corre el texto).</summary>
    private async Task ShowNotesAsync(DocumentTab tab, SpeechDocument document)
    {
        List<DocumentNote> notes;
        try
        {
            notes = Notes.Load(tab.State.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            notes = [];
        }

        _notes = new NotesContext(tab, document, DocumentOutline.Build(document), notes);
        _lastDeletedNote = null;
        UpdateNotesButton();
        if (notes.Count > 0 && _view is not null)
        {
            await _view.SetNotesAsync(notes, _settings.ShowNotes);
        }
    }

    private void ClearNotes()
    {
        _notes = null;
        _lastDeletedNote = null;
        UpdateNotesButton();
    }

    private void OnNoteRequested(NoteRequest request)
    {
        if (_notes is not { } context || _view is null)
        {
            return;
        }

        switch (request)
        {
            case NoteRequest.Create create:
                CreateNote(context, create);
                break;
            case NoteRequest.Update update when Find(update.Id) is { } note:
                note.Text = update.Text ?? note.Text;
                note.Kind = update.Kind ?? note.Kind;
                note.Resolved = update.Resolved ?? note.Resolved;
                SaveNotes(context);
                break;
            case NoteRequest.Delete delete when Find(delete.Id) is { } note:
                context.Notes.Remove(note);
                _lastDeletedNote = delete.Silent ? null : note;
                SaveNotes(context);
                break;
            case NoteRequest.Restore restore when _lastDeletedNote is { } deleted && deleted.Id == restore.Id:
                context.Notes.Add(deleted);
                _lastDeletedNote = null;
                SaveNotes(context);
                _ = _view.SetNotesAsync(context.Notes, _settings.ShowNotes);
                break;
            case NoteRequest.Anchored anchored:
                UpdateLocations(context, anchored.Results);
                break;
            case NoteRequest.Export:
                ExportNotes(context);
                break;
            case NoteRequest.ClearResolved:
                int removed = context.Notes.RemoveAll(n => n.Resolved);
                SaveNotes(context);
                _ = _view.SetNotesAsync(context.Notes, _settings.ShowNotes);
                ShowToast(removed == 1 ? "Se borró 1 apunte resuelto" : $"Se borraron {removed} apuntes resueltos");
                break;
            case NoteRequest.NoSelection:
                CreateNoteForReadingParagraph();
                break;
        }

        DocumentNote? Find(string id) => context.Notes.Find(n => n.Id == id);
    }

    private void CreateNote(NotesContext context, NoteRequest.Create create)
    {
        NoteLocation location = NoteReferences.Describe(context.Document, context.Outline, create.StartAnchor, create.EndAnchor);
        var note = new DocumentNote
        {
            Id = Guid.NewGuid().ToString("N"),
            Quote = create.Quote,
            Line = location.Line,
            EndLine = location.EndLine,
            Section = location.Section,
            CreatedUtc = DateTime.UtcNow,
        };
        context.Notes.Add(note);
        SaveNotes(context);

        if (!_settings.ShowNotes)
        {
            _settings.ShowNotes = true; // se creó uno: la columna tiene que verse para escribirlo
            _settingsStore.Save(_settings);
        }

        PronouncePopup.IsOpen = false;
        _ = _view?.SetNotesAsync(context.Notes, visible: true, editId: note.Id);
    }

    /// <summary>Ctrl+Alt+M sin selección: el párrafo que se está leyendo (o donde quedó la lectura).</summary>
    private void CreateNoteForReadingParagraph()
    {
        int id = _player.CurrentSegmentId;
        if (_view is not null && _document is { } document && id >= 0 && id < document.Segments.Count)
        {
            _ = _view.CreateNoteForSegmentAsync(document.Segments[id].AnchorId);
        }
        else
        {
            ShowToast("Selecciona un texto (o empieza a leer) para agregar un apunte.", ok: false);
        }
    }

    /// <summary>Clic en "Apunte" junto a la selección.</summary>
    private async void OnNoteChipClick(object sender, RoutedEventArgs e)
    {
        PronouncePopup.IsOpen = false;
        if (_view is not null && !await _view.CreateNoteFromSelectionAsync())
        {
            ShowToast("La selección ya no está: vuelve a seleccionar el texto.", ok: false);
        }
    }

    /// <summary>Las citas se volvieron a buscar al abrir: líneas y sección al día, y cuáles ya no están.</summary>
    private void UpdateLocations(NotesContext context, IReadOnlyList<NoteAnchor> results)
    {
        bool changed = false;
        foreach (NoteAnchor result in results)
        {
            if (context.Notes.Find(n => n.Id == result.Id) is not { } note)
            {
                continue;
            }

            note.Orphaned = !result.Found;
            if (result is { Found: true, StartAnchor: { } start })
            {
                NoteLocation location = NoteReferences.Describe(context.Document, context.Outline, start, result.EndAnchor ?? start);
                if (location.Line > 0 && (location.Line, location.EndLine, location.Section) != (note.Line, note.EndLine, note.Section))
                {
                    (note.Line, note.EndLine, note.Section) = (location.Line, location.EndLine, location.Section);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            SaveNotes(context);
        }
    }

    private void ExportNotes(NotesContext context)
    {
        int pending = context.Notes.Count(n => !n.Resolved);
        if (pending == 0)
        {
            ShowToast("No hay apuntes pendientes para copiar.", ok: false);
            return;
        }

        var source = new NoteSource(context.Tab.State.IsPasted ? null : context.Path, context.Document.Title);
        CopyToClipboard(NoteExporter.ToMarkdown(_settings.NotesInstruction, source, context.Notes),
            pending == 1 ? "1 apunte copiado: pégalo en el chat de la IA" : $"{pending} apuntes copiados: pégalos en el chat de la IA");
    }

    private void SaveNotes(NotesContext context)
    {
        try
        {
            Notes.Save(context.Path, context.Notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudieron guardar los apuntes.", ok: false);
        }

        UpdateNotesButton();
    }

    /// <summary>Un texto pegado que se guardó como archivo se lleva sus apuntes.</summary>
    private void MoveNotes(string fromPath, string toPath)
    {
        try
        {
            Notes.Move(fromPath, toPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }

    /// <summary>Cerrar un texto pegado lo borra, y con él sus apuntes.</summary>
    private void DeleteNotes(string path)
    {
        try
        {
            Notes.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }

    private void OnNotesToggleClick(object sender, RoutedEventArgs e) => ToggleNotes();

    private void ToggleNotes()
    {
        if (_notes is not { Notes.Count: > 0 })
        {
            ShowToast("Selecciona un texto y elige «Apunte» (o Ctrl+Alt+M) para agregar uno.");
            return;
        }

        _settings.ShowNotes = !_settings.ShowNotes;
        _settingsStore.Save(_settings);
        UpdateNotesButton();
        _ = _view?.SetNotesVisibleAsync(_settings.ShowNotes);
    }

    /// <summary>El botón de la barra: cuántos pendientes hay y si la columna se ve.</summary>
    private void UpdateNotesButton()
    {
        int total = _notes?.Notes.Count ?? 0;
        int pending = _notes?.Notes.Count(n => !n.Resolved) ?? 0;
        NotesToggle.IsEnabled = _notes is not null;
        NotesCount.Text = pending.ToString(Mexico);
        NotesCountBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool shown = total > 0 && _settings.ShowNotes;
        NotesIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, shown ? "LectorAccentBrush" : "LectorTextBrush");
        string tip = total == 0
            ? "Apuntes: selecciona un texto y elige «Apunte» (Ctrl+Alt+M) para pedirle a una IA que lo aclare o mejore"
            : $"{(shown ? "Ocultar" : "Mostrar")} los apuntes ({pending} pendiente{(pending == 1 ? "" : "s")} de {total})";
        NotesToggle.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(NotesToggle, $"Apuntes, {pending} pendientes");
    }
}
