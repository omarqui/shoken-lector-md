using System.Text.Json;
using Shoken.App.Services;
using Shoken.Core.Audio;
using Shoken.Core.Demo;
using Shoken.Core.Model;
using Shoken.Core.Planning;
using Shoken.Core.Settings;
using Shoken.Core.Tts;

namespace Shoken.App;

// Demo de entonación: la pestaña «Así entona Shoken». La página (demo.js) reproduce y dibuja; aquí se arma con la
// grabación incluida y se genera «Tu voz» con la cadena de motores de la app (misma caché y misma cuota).
public partial class MainWindow
{
    private static readonly JsonSerializerOptions DemoJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private CancellationTokenSource? _demoVoiceCts; // generando «Tu voz»
    private DemoVoice? _demoVoice;                  // «Tu voz» ya generada con la configuración actual

    /// <summary>Una lectura lista para la página: su URL en app.lector, la duración y el inicio de cada segmento.</summary>
    private sealed record DemoTakeData(string Src, double Duration, IReadOnlyList<double> Marks);

    /// <summary>«Tu voz»: las dos lecturas con el motor del usuario. Con Kokoro o Piper no cambia el tono.</summary>
    private sealed record DemoVoice(string Engine, string Label, bool Local, DemoTakeData Con, DemoTakeData Sin);

    /// <summary>Abre la pestaña de la demo, o va a la que ya está abierta.</summary>
    /// <param name="force">Vuelve a mostrarla aunque ya sea la activa (cambió la configuración).</param>
    private async Task OpenDemoAsync(bool force = false)
    {
        DocumentTab tab = _tabs.FirstOrDefault(t => t.IsDemo) ?? AddTab(new DocumentTab(new OpenTab()) { IsDemo = true }, after: _active);
        await ActivateAsync(tab, force);
    }

    private async Task ShowDemoTabAsync(int activation)
    {
        if (_view is null || _services is null)
        {
            return;
        }

        _document = null;
        _chunks = [];
        RebuildTimeline();
        Player.SetPosition(HtmlPages.DemoTitle, "La demo tiene su propio reproductor, arriba de la página (Espacio para escuchar)");
        ShowOutline(null);
        SetDocumentHeader("", null);
        Title = $"{HtmlPages.DemoTitle} — Shoken";

        // El texto, las notas y la grabación salen del plan por omisión, el mismo con que se grabó.
        DemoPlan plan = IntonationDemo.RecordedPlan();
        await _view.ShowDemoAsync(plan.Document, DemoPageData(plan));
        if (activation != _activation)
        {
            return;
        }

        _shownActivation = activation;
        UpdateButtons();
        SaveSession();
    }

    private string DemoPageData(DemoPlan plan)
    {
        DemoRecording? recording = IntonationDemo.Recording;
        string? engine = _services?.Engine.ExpectedEngineName();
        var data = new
        {
            Segments = IntonationDemo.Describe(plan).Select(s => new
            {
                s.Id,
                Anchor = s.AnchorId,
                Kind = s.Kind.ToString(),
                Level = s.HeadingLevel,
                s.Text,
                Pause = s.PauseBeforeMs,
                Prosody = Change(s.Prosody),
                s.Strong,
                StrongProsody = Change(s.StrongProsody),
                s.Quoted,
                QuotedProsody = Change(s.QuotedProsody),
                s.Asides,
                AsideProsody = Change(s.AsideProsody),
                Period = s.AddsPeriod,
                Number = s.ReadsNumber,
                s.Announces,
            }),
            Recorded = recording is null ? null : new
            {
                Label = "Dalia (grabada)",
                Con = Take(recording.Con),
                Sin = Take(recording.Sin),
            },
            Cost = recording is null ? null : new { Text = recording.TextCharacters, Intonation = recording.IntonationCharacters },
            Engine = new
            {
                Available = engine is not null,
                Label = engine is null ? null : FriendlyEngineName(engine),
                Local = engine is not null && IsLocalEngine(engine),
                Reason = engine is null ? "Para oír la demo con tu voz, configura Azure, Google, Kokoro o Piper en ⚙ Configuración." : null,
            },
            Mine = _demoVoice is { } mine && mine.Engine == engine ? mine : null,
            Speed = _settings.PlaybackSpeed,
        };
        return JsonSerializer.Serialize(data, DemoJson);

        static object? Change(ProsodyChange? change) => change is null ? null : new { Rate = change.RatePercent, Pitch = change.PitchPercent };

        static DemoTakeData Take(DemoTake take) =>
            new($"https://{HtmlPages.AppHost}/{take.File}", take.DurationSeconds, take.Marks);
    }

    /// <summary>Pedidos de la página: "voice" (eligió «Tu voz»), "generate" (confirmó el costo) y "cancel".</summary>
    private void OnDemoRequested(string action)
    {
        if (_active is not { IsDemo: true })
        {
            return;
        }

        switch (action)
        {
            case "voice": _ = PrepareDemoVoiceAsync(confirmed: false); break;
            case "generate": _ = PrepareDemoVoiceAsync(confirmed: true); break;
            case "cancel": _demoVoiceCts?.Cancel(); break;
        }
    }

    private async Task PrepareDemoVoiceAsync(bool confirmed)
    {
        if (_services is null || _view is null || _demoVoiceCts is not null)
        {
            return; // ya se está generando: la página recibe el avance
        }

        string? engine = _services.Engine.ExpectedEngineName();
        if (engine is null)
        {
            await _view.CallDemoAsync("voiceFailed", new { message = "No hay ninguna voz configurada: agrégala en ⚙ Configuración.", retry = false });
            return;
        }

        if (_demoVoice is { } ready && ready.Engine == engine)
        {
            await _view.CallDemoAsync("voiceReady", ready);
            return;
        }

        // Con tu configuración: tu diccionario, tu velocidad y las opciones de lectura.
        SpeechDocument document = _services.Parse(IntonationDemo.Markdown, null);
        IReadOnlyList<SpeechChunk> chunks = _services.Plan(document);
        List<SpeechChunk> all = [.. chunks, IntonationDemo.FlatChunk(document)];

        // Antes de gastar cuota de la nube, la página pregunta.
        if (!confirmed && _services.Engine.EstimateCloudCost(all) is { Characters: > 0 } cost)
        {
            await _view.CallDemoAsync("voiceConfirm", new { provider = cost.Label, characters = cost.Characters, remaining = cost.Remaining });
            return;
        }

        int activation = _activation;
        string label = FriendlyEngineName(engine);
        using var cts = new CancellationTokenSource();
        _demoVoiceCts = cts;
        try
        {
            var audio = new List<AudioChunk>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                await NotifyDemoAsync(activation, "voiceProgress", new { done = i, total = all.Count, label });
                audio.Add(await _services.Engine.SynthesizeAsync(all[i], cts.Token));
            }

            // Si la cadena cambió de motor a mitad (por ejemplo, Azure se saturó y siguió Kokoro), no se compara.
            List<string> voices = audio.Select(a => a.EngineName).Distinct().ToList();
            if (voices.Count > 1)
            {
                throw new TtsException(TtsFailureKind.Other,
                    $"no salió todo de la misma voz ({string.Join(" y ", voices.Select(FriendlyEngineName))}), así que no se puede comparar");
            }

            DemoVoice voice = PublishDemoVoice(document, audio.Take(audio.Count - 1).ToList(), audio[^1], voices[0]);
            _demoVoice = voice;
            await NotifyDemoAsync(activation, "voiceReady", voice);
        }
        catch (OperationCanceledException)
        {
            await NotifyDemoAsync(activation, "voiceCanceled", null);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            string reason = ex is TtsException tts ? tts.Message : ex.Message;
            await NotifyDemoAsync(activation, "voiceFailed", new { message = $"No se pudo generar tu voz: {reason}.", retry = true });
        }
        finally
        {
            _demoVoiceCts = null;
        }
    }

    /// <summary>Solo si la demo que pidió algo sigue a la vista (no se cambió de pestaña mientras tanto).</summary>
    private Task NotifyDemoAsync(int activation, string function, object? argument) =>
        _view is not null && _active is { IsDemo: true } && activation == _activation
            ? _view.CallDemoAsync(function, argument)
            : Task.CompletedTask;

    /// <summary>Une los bloques de «Con Shoken» en un archivo y deja los dos donde la página los puede reproducir.</summary>
    private DemoVoice PublishDemoVoice(SpeechDocument document, IReadOnlyList<AudioChunk> con, AudioChunk flat, string engine)
    {
        var marks = new double[document.Segments.Count];
        var parts = new List<byte[]>(con.Count);
        double offset = 0;
        foreach (AudioChunk chunk in con)
        {
            byte[] bytes = File.ReadAllBytes(chunk.FilePath);
            foreach (SegmentMark mark in chunk.Marks.Where(m => m.SegmentId < marks.Length))
            {
                marks[mark.SegmentId] = Math.Round(offset + mark.Offset.TotalSeconds, 3);
            }

            parts.Add(bytes);
            offset += (chunk.Duration ?? Measure(bytes, chunk.FilePath)).TotalSeconds;
        }

        string extension = Path.GetExtension(con[0].FilePath).ToLowerInvariant();
        byte[] joined = extension == ".wav"
            ? WavAudio.Concatenate(parts.Select(p => (WavAudio.Parse(p), 0)).ToList(), out _).ToBytes()
            : Mp3Audio.Concatenate(parts);
        string conUrl = _view!.PublishDemoAudio("con", joined, extension);

        // La versión plana es un solo bloque sin marcas por segmento: el inicio de cada uno se estima.
        byte[] flatBytes = File.ReadAllBytes(flat.FilePath);
        TimeSpan flatDuration = flat.Duration ?? Measure(flatBytes, flat.FilePath);
        string sinUrl = _view.PublishDemoAudio("sin", flatBytes, Path.GetExtension(flat.FilePath).ToLowerInvariant());
        var flatMarks = IntonationDemo.EstimateStarts(document, flatDuration).Select(t => Math.Round(t.TotalSeconds, 3)).ToList();

        return new DemoVoice(engine, FriendlyEngineName(engine), IsLocalEngine(engine),
            new DemoTakeData(conUrl, Math.Round(offset, 3), marks),
            new DemoTakeData(sinUrl, Math.Round(flatDuration.TotalSeconds, 3), flatMarks));

        static TimeSpan Measure(byte[] bytes, string path) =>
            Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ? WavAudio.Parse(bytes).Duration : Mp3Audio.Duration(bytes);
    }

    /// <summary>Otra configuración de voz: «Tu voz» ya generada deja de servir (y lo que se generaba, también).</summary>
    private void ForgetDemoVoice()
    {
        _demoVoiceCts?.Cancel();
        _demoVoice = null;
    }

    /// <summary>La ventana se ocultó o se minimizó: la demo no sigue sonando.</summary>
    private void PauseDemo()
    {
        if (_active is { IsDemo: true } && _view is not null)
        {
            _ = _view.CallDemoAsync("pause");
        }
    }
}
