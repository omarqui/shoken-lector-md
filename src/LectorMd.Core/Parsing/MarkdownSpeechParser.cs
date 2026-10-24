using System.Globalization;
using System.Text.RegularExpressions;
using LectorMd.Core.Model;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LectorMd.Core.Parsing;

/// <summary>
/// Convierte Markdown en una lista de <see cref="SpeechSegment"/> (qué leer, con qué pausas y estilos)
/// y en HTML con atributos <c>data-seg</c> para resaltar lo que se está leyendo.
/// </summary>
public sealed class MarkdownSpeechParser
{
    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> LineBreakTags = new(StringComparer.OrdinalIgnoreCase) { "<br>", "<br/>", "<br />" };

    private readonly MarkdownSpeechOptions _options;
    private readonly MarkdownPipeline _pipeline;

    public MarkdownSpeechParser(MarkdownSpeechOptions? options = null)
    {
        _options = options ?? new MarkdownSpeechOptions();
        _pipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UseAdvancedExtensions()
            .Build();
    }

    public MarkdownSpeechOptions Options => _options;

    public SpeechDocument Parse(string markdown, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument document = Markdig.Markdown.Parse(markdown, _pipeline);
        var state = new State(_options, markdown);
        VisitChildren(document, state, SegmentKind.Paragraph);

        string title = state.Segments.FirstOrDefault(s => s.IsHeading)?.PlainText is { Length: > 0 } heading
            ? heading
            : sourcePath is null ? "Documento" : Path.GetFileNameWithoutExtension(sourcePath);

        return new SpeechDocument
        {
            Title = title,
            Segments = state.Segments,
            HtmlBody = RenderHtml(document),
            SourcePath = sourcePath,
        };
    }

    private string RenderHtml(MarkdownDocument document)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return AlertTitle.Replace(writer.ToString(), m => m.Groups[1].Value + AlertLabel(m.Groups[2].Value).TrimEnd('.') + "</p>");
    }

    // Markdig rotula las alertas en inglés ("Warning"); se traducen para mostrarlas.
    private static readonly Regex AlertTitle = new(
        "(<p class=\"markdown-alert-title\">(?:(?!</p>).)*?</svg>)(Note|Tip|Important|Warning|Caution)</p>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ---------------------------------------------------------------- bloques

    private void VisitChildren(ContainerBlock container, State state, SegmentKind kind)
    {
        foreach (Block child in container)
        {
            Visit(child, state, kind);
        }
    }

    private void Visit(Block block, State state, SegmentKind kind)
    {
        PauseProfile pauses = _options.Pauses;

        switch (block)
        {
            case YamlFrontMatterBlock:
            case HtmlBlock:
            case LinkReferenceDefinitionGroup:
            case FootnoteGroup:
                return;

            case HeadingBlock heading:
            {
                int level = Math.Clamp(heading.Level, 1, 6);
                var runs = new List<SpeechRun>();
                CollectInlines(heading.Inline, RunStyle.None, runs);
                AddSegments(state, heading, SegmentKind.Heading, runs,
                    pauses.HeadingBefore[level - 1], pauses.HeadingAfter[level - 1], level);
                return;
            }

            case ThematicBreakBlock:
                state.PendingPauseMs += pauses.ThematicBreak;
                return;

            case AlertBlock alert:
                state.PendingPauseMs += pauses.QuoteEdge;
                AddSegments(state, alert, SegmentKind.AlertLabel, [new SpeechRun(AlertLabel(alert.Kind.ToString()))], 0, 150);
                VisitChildren(alert, state, SegmentKind.Quote);
                state.PendingPauseMs += pauses.QuoteEdge;
                return;

            case QuoteBlock quote:
                state.PendingPauseMs += pauses.QuoteEdge;
                VisitChildren(quote, state, SegmentKind.Quote);
                state.PendingPauseMs += pauses.QuoteEdge;
                return;

            case ListBlock list:
                VisitList(list, state);
                return;

            case MathBlock math:
                AddSegments(state, math, SegmentKind.Math, [new SpeechRun("Fórmula.")], 200, pauses.CodeBlock);
                return;

            case FencedCodeBlock fenced:
                VisitCode(fenced, fenced.Info, state);
                return;

            case CodeBlock code:
                VisitCode(code, null, state);
                return;

            case Table table:
                VisitTable(table, state);
                return;

            case ParagraphBlock paragraph:
            {
                var runs = new List<SpeechRun>();
                CollectInlines(paragraph.Inline, RunStyle.None, runs);
                int after = kind == SegmentKind.ListItem ? pauses.ListItem : pauses.Paragraph;
                AddSegments(state, paragraph, kind, runs, 0, after);
                return;
            }

            case LeafBlock leaf when leaf.Inline is not null:
            {
                // Términos de definición, pies de figura, etc.
                var runs = new List<SpeechRun>();
                CollectInlines(leaf.Inline, RunStyle.None, runs);
                AddSegments(state, leaf, kind, runs, 0, pauses.Paragraph);
                return;
            }

            case ContainerBlock container:
                VisitChildren(container, state, kind);
                return;
        }
    }

    private void VisitList(ListBlock list, State state)
    {
        // Con listas "sueltas" Markdig envuelve cada elemento en <p>, que es donde ponemos data-seg.
        list.IsLoose = true;

        bool numeric = list.IsOrdered;
        int number = 1;
        if (numeric && !int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            numeric = false; // listas con letras o romanos: sin prefijo hablado
        }

        foreach (ListItemBlock item in list.OfType<ListItemBlock>())
        {
            bool first = true;
            foreach (Block child in item)
            {
                if (first && child is ParagraphBlock paragraph)
                {
                    var runs = new List<SpeechRun>();
                    if (numeric)
                    {
                        runs.Add(new SpeechRun(number.ToString(CultureInfo.InvariantCulture) + ". "));
                    }

                    CollectInlines(paragraph.Inline, RunStyle.None, runs);
                    AddSegments(state, paragraph, SegmentKind.ListItem, runs, 0, _options.Pauses.ListItem);
                }
                else
                {
                    Visit(child, state, SegmentKind.ListItem);
                }

                first = false;
            }

            number++;
        }

        state.PendingPauseMs += _options.Pauses.ListEnd;
    }

    private void VisitCode(CodeBlock code, string? info, State state)
    {
        PauseProfile pauses = _options.Pauses;
        if (_options.CodeBlocks == CodeBlockMode.Skip)
        {
            state.PendingPauseMs += 300;
            return;
        }

        string language = (info ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (language.Equals("mermaid", StringComparison.OrdinalIgnoreCase))
        {
            AddSegments(state, code, SegmentKind.CodeBlock, [new SpeechRun("Diagrama.")], 200, pauses.CodeBlock);
            return;
        }

        string friendly = FriendlyLanguage(language);
        if (_options.CodeBlocks == CodeBlockMode.Announce)
        {
            string text = friendly.Length > 0 ? $"Bloque de código en {friendly}." : "Bloque de código.";
            AddSegments(state, code, SegmentKind.CodeBlock, [new SpeechRun(text)], 200, pauses.CodeBlock);
            return;
        }

        var lines = code.Lines.ToString()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var runs = new List<SpeechRun> { new(friendly.Length > 0 ? $"Código en {friendly}: " : "Código: ") };
        runs.AddRange(lines.Select(l => new SpeechRun(l.TrimEnd('.', ';') + ". ", RunStyle.Code)));
        AddSegments(state, code, SegmentKind.CodeBlock, runs, 200, pauses.CodeBlock);
    }

    private void VisitTable(Table table, State state)
    {
        PauseProfile pauses = _options.Pauses;
        if (_options.Tables == TableMode.Skip)
        {
            state.PendingPauseMs += 300;
            return;
        }

        var rows = table.OfType<TableRow>().ToList();
        TableRow? headerRow = rows.FirstOrDefault(r => r.IsHeader);
        List<string> headers = headerRow is null
            ? []
            : headerRow.OfType<TableCell>().Select(CellText).ToList();
        int dataRows = rows.Count(r => !r.IsHeader);
        int columns = Math.Max(headers.Count, rows.Select(r => r.Count).DefaultIfEmpty(0).Max());

        if (_options.Tables == TableMode.Announce)
        {
            string announce = $"Tabla de {columns} {(columns == 1 ? "columna" : "columnas")} y {dataRows} {(dataRows == 1 ? "fila" : "filas")}.";
            AddSegments(state, table, SegmentKind.TableHeader, [new SpeechRun(announce)], 250, 400);
            return;
        }

        var named = headers.Where(h => h.Length > 0).ToList();
        string intro = named.Count > 0
            ? $"Tabla con columnas: {string.Join(", ", named)}."
            : $"Tabla de {columns} {(columns == 1 ? "columna" : "columnas")}.";
        AddSegments(state, (Block?)headerRow ?? table, SegmentKind.TableHeader, [new SpeechRun(intro)], 250, 350);

        foreach (TableRow row in rows.Where(r => !r.IsHeader))
        {
            var runs = new List<SpeechRun>();
            int column = 0;
            foreach (TableCell cell in row.OfType<TableCell>())
            {
                var cellRuns = new List<SpeechRun>();
                foreach (Block child in cell)
                {
                    if (child is LeafBlock leaf)
                    {
                        CollectInlines(leaf.Inline, RunStyle.None, cellRuns);
                    }
                }

                string cellText = string.Concat(cellRuns.Select(r => r.Text)).Trim();
                if (cellText.Length > 0)
                {
                    string header = column < headers.Count ? headers[column] : "";
                    if (header.Length > 0)
                    {
                        runs.Add(new SpeechRun(header + ": "));
                    }

                    runs.AddRange(cellRuns);
                    runs.Add(new SpeechRun(EndsWithPunctuation(cellText) ? " " : ". "));
                }

                column++;
            }

            AddSegments(state, row, SegmentKind.TableRow, runs, 0, pauses.TableRow);
        }
    }

    private string CellText(TableCell cell)
    {
        var runs = new List<SpeechRun>();
        foreach (Block child in cell)
        {
            if (child is LeafBlock leaf)
            {
                CollectInlines(leaf.Inline, RunStyle.None, runs);
            }
        }

        return Whitespace.Replace(string.Concat(runs.Select(r => r.Text)), " ").Trim();
    }

    // ---------------------------------------------------------------- inlines

    private void CollectInlines(ContainerInline? container, RunStyle style, List<SpeechRun> runs)
    {
        if (container is null)
        {
            return;
        }

        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    runs.Add(new SpeechRun(literal.Content.ToString(), style));
                    break;

                case CodeInline code:
                    runs.Add(new SpeechRun(code.Content, style | RunStyle.Code));
                    break;

                case TaskList task:
                    runs.Add(new SpeechRun(task.Checked ? "Hecho: " : "Pendiente: ", style));
                    break;

                case LineBreakInline:
                    runs.Add(new SpeechRun(" ", style));
                    break;

                case HtmlEntityInline entity:
                    runs.Add(new SpeechRun(entity.Transcoded.ToString(), style));
                    break;

                case AutolinkInline autolink:
                    runs.Add(new SpeechRun(autolink.IsEmail ? autolink.Url : "enlace", style | RunStyle.Link));
                    break;

                case MathInline:
                    runs.Add(new SpeechRun("fórmula", style));
                    break;

                case AbbreviationInline abbreviation:
                    runs.Add(new SpeechRun(abbreviation.Abbreviation.Label ?? "", style));
                    break;

                case FootnoteLink:
                    break;

                case HtmlInline html:
                    if (LineBreakTags.Contains(html.Tag.Trim()))
                    {
                        runs.Add(new SpeechRun(" ", style));
                    }

                    break;

                case LinkInline { IsImage: true } image:
                    if (_options.ReadImageAltText)
                    {
                        var alt = new List<SpeechRun>();
                        CollectInlines(image, style, alt);
                        string altText = string.Concat(alt.Select(r => r.Text)).Trim();
                        if (altText.Length > 0)
                        {
                            runs.Add(new SpeechRun($"Imagen: {altText}. ", style));
                        }
                    }

                    break;

                case LinkInline link:
                {
                    int before = runs.Count;
                    CollectInlines(link, style | RunStyle.Link, runs);
                    bool hasText = runs.Skip(before).Any(r => !string.IsNullOrWhiteSpace(r.Text));
                    if (!hasText && !string.IsNullOrEmpty(link.Url))
                    {
                        runs.Add(new SpeechRun("enlace", style | RunStyle.Link));
                    }

                    break;
                }

                case EmphasisInline emphasis:
                    CollectInlines(emphasis, style | EmphasisStyle(emphasis), runs);
                    break;

                case ContainerInline nested:
                    CollectInlines(nested, style, runs);
                    break;
            }
        }
    }

    private static RunStyle EmphasisStyle(EmphasisInline emphasis) => emphasis.DelimiterChar switch
    {
        '*' or '_' => emphasis.DelimiterCount >= 2 ? RunStyle.Strong : RunStyle.Emphasis,
        '=' => RunStyle.Strong, // ==resaltado==
        _ => RunStyle.None,     // tachado, subíndice, superíndice, insertado
    };

    // ---------------------------------------------------------------- segmentos

    private static void AddSegments(State state, Block block, SegmentKind kind, List<SpeechRun> runs,
        int pauseBefore, int pauseAfter, int headingLevel = 0)
    {
        List<SpeechRun> cleaned = CleanRuns(runs);
        if (cleaned.Count == 0)
        {
            return;
        }

        string anchor = state.NewAnchor(block);
        List<List<SpeechRun>> parts = kind == SegmentKind.Heading
            ? [cleaned]
            : SplitLong(cleaned, state.Options.LongSegmentChars);

        for (int i = 0; i < parts.Count; i++)
        {
            state.Segments.Add(new SpeechSegment
            {
                Id = state.Segments.Count,
                AnchorId = anchor,
                Kind = kind,
                HeadingLevel = headingLevel,
                Runs = parts[i],
                PauseBeforeMs = i == 0 ? pauseBefore + state.TakePendingPause() : 0,
                PauseAfterMs = i == parts.Count - 1 ? pauseAfter : 0,
                Line = block.Line + 1,
                EndLine = block.Span.End >= block.Span.Start && block.Span.Start >= 0 ? state.LineAt(block.Span.End) : block.Line + 1,
            });
        }
    }

    /// <summary>Normaliza espacios, une tramos contiguos del mismo estilo y descarta los vacíos.</summary>
    internal static List<SpeechRun> CleanRuns(IEnumerable<SpeechRun> runs)
    {
        var merged = new List<SpeechRun>();
        foreach (SpeechRun run in runs)
        {
            if (string.IsNullOrEmpty(run.Text))
            {
                continue;
            }

            string text = Whitespace.Replace(run.Text, " ");
            if (merged.Count > 0 && merged[^1].Style == run.Style)
            {
                merged[^1] = merged[^1] with { Text = Whitespace.Replace(merged[^1].Text + text, " ") };
            }
            else
            {
                merged.Add(new SpeechRun(text, run.Style));
            }
        }

        for (int i = 1; i < merged.Count; i++)
        {
            if (merged[i - 1].Text.EndsWith(' ') && merged[i].Text.StartsWith(' '))
            {
                merged[i] = merged[i] with { Text = merged[i].Text.TrimStart() };
            }
        }

        merged.RemoveAll(r => r.Text.Length == 0);
        while (merged.Count > 0 && string.IsNullOrWhiteSpace(merged[0].Text))
        {
            merged.RemoveAt(0);
        }

        while (merged.Count > 0 && string.IsNullOrWhiteSpace(merged[^1].Text))
        {
            merged.RemoveAt(merged.Count - 1);
        }

        if (merged.Count > 0)
        {
            merged[0] = merged[0] with { Text = merged[0].Text.TrimStart() };
            merged[^1] = merged[^1] with { Text = merged[^1].Text.TrimEnd() };
        }

        return merged;
    }

    /// <summary>Divide un párrafo muy largo por oraciones para no generar bloques de audio enormes.</summary>
    internal static List<List<SpeechRun>> SplitLong(List<SpeechRun> runs, int maxChars)
    {
        int total = runs.Sum(r => r.Text.Length);
        if (maxChars <= 0 || total <= maxChars)
        {
            return [runs];
        }

        int target = Math.Max(200, maxChars * 2 / 3);
        var result = new List<List<SpeechRun>>();
        var current = new List<SpeechRun>();
        int length = 0;

        foreach (SpeechRun run in runs)
        {
            string[] pieces = SentenceBoundary.Split(run.Text);
            for (int p = 0; p < pieces.Length; p++)
            {
                bool boundary = p < pieces.Length - 1;
                string piece = boundary ? pieces[p] + " " : pieces[p];
                if (piece.Length == 0)
                {
                    continue;
                }

                current.Add(new SpeechRun(piece, run.Style));
                length += piece.Length;

                if (boundary && length >= target)
                {
                    AddIfNotEmpty(result, CleanRuns(current));
                    current = [];
                    length = 0;
                }
            }
        }

        AddIfNotEmpty(result, CleanRuns(current));
        return result;

        static void AddIfNotEmpty(List<List<SpeechRun>> target, List<SpeechRun> part)
        {
            if (part.Count > 0)
            {
                target.Add(part);
            }
        }
    }

    // ---------------------------------------------------------------- utilidades

    private static bool EndsWithPunctuation(string text) =>
        text.Length > 0 && ".!?:;…".Contains(text[^1]);

    private static string AlertLabel(string kind) => kind.ToUpperInvariant() switch
    {
        "TIP" => "Consejo.",
        "IMPORTANT" => "Importante.",
        "WARNING" => "Advertencia.",
        "CAUTION" => "Precaución.",
        _ => "Nota.",
    };

    internal static string FriendlyLanguage(string language) => language.ToLowerInvariant() switch
    {
        "" or "text" or "txt" or "plaintext" => "",
        "cs" or "csharp" or "c#" => "C#",
        "fs" or "fsharp" or "f#" => "F#",
        "vb" or "vbnet" => "Visual Basic",
        "js" or "javascript" or "jsx" => "JavaScript",
        "ts" or "typescript" or "tsx" => "TypeScript",
        "py" or "python" => "Python",
        "sql" or "tsql" or "mssql" => "SQL",
        "json" or "jsonc" => "JSON",
        "xml" or "xaml" or "csproj" => "XML",
        "yaml" or "yml" => "YAML",
        "html" => "HTML",
        "css" or "scss" => "CSS",
        "sh" or "bash" or "shell" or "zsh" => "bash",
        "ps" or "ps1" or "powershell" or "pwsh" => "PowerShell",
        "cmd" or "bat" or "batch" => "consola de Windows",
        "dockerfile" or "docker" => "Docker",
        "md" or "markdown" => "Markdown",
        "java" => "Java",
        "go" or "golang" => "Go",
        "rust" or "rs" => "Rust",
        "kotlin" or "kt" => "Kotlin",
        "php" => "PHP",
        "razor" or "cshtml" => "Razor",
        "http" or "rest" => "HTTP",
        "diff" or "patch" => "diff",
        _ => language,
    };

    private sealed class State(MarkdownSpeechOptions options, string markdown)
    {
        private int _anchorCounter;
        private readonly int[] _lineStarts = LineStarts(markdown);

        public MarkdownSpeechOptions Options { get; } = options;

        /// <summary>Línea (1-based) del carácter en <paramref name="offset"/> del Markdown.</summary>
        public int LineAt(int offset)
        {
            int index = Array.BinarySearch(_lineStarts, offset);
            return (index >= 0 ? index : ~index - 1) + 1;
        }

        private static int[] LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts.ToArray();
        }

        public List<SpeechSegment> Segments { get; } = [];

        /// <summary>Pausa acumulada que se aplicará antes del próximo segmento.</summary>
        public int PendingPauseMs { get; set; }

        public int TakePendingPause()
        {
            int pause = PendingPauseMs;
            PendingPauseMs = 0;
            return pause;
        }

        public string NewAnchor(Block block)
        {
            string id = "s" + (++_anchorCounter).ToString(CultureInfo.InvariantCulture);
            block.GetAttributes().AddProperty("data-seg", id);
            // Para los apuntes: si una cita se repite, la página elige la aparición más cercana a su línea.
            block.GetAttributes().AddProperty("data-line", (block.Line + 1).ToString(CultureInfo.InvariantCulture));
            return id;
        }
    }
}
