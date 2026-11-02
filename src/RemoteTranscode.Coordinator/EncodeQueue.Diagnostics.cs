using RemoteTranscode.Common;

namespace RemoteTranscode.Coordinator;

/// <summary>What a failure report needs from the queue: a copy of the conversion's lists, and the spool kept while it is analysed.</summary>
public sealed partial class EncodeQueue
{
    private readonly SpoolPins spoolPins = new();
    // Cancelled when the host stops (RunAsync): the analysis ends and sends what it has.
    private readonly CancellationTokenSource diagnosticsStop = new();
    private FailureDiagnostics? failureDiagnostics;

    /// <summary>
    /// Copies what the analysis needs, under the lock. The chunks are measured once the conversion has ended (assembly
    /// rejected, given back) and its spool is then pinned; a conversion still running keeps its spool to itself.
    /// </summary>
    private DiagnosticsRequest PrepareDiagnostics(EncodeTask task, FailureKind kind, string error, string? check)
    {
        bool measure = kind is FailureKind.AssemblyRejected or FailureKind.GivenBack;
        lock (gate)
        {
            failureDiagnostics ??= new FailureDiagnostics(media.ProbeQuietlyAsync, media.FfmpegQuietlyAsync);
            var slices = task.Slices.OrderBy(s => s.Index).Select(s =>
            {
                var durations = task.ConcatDurations(s).ToDictionary(d => d.Index, d => d.Duration);
                var chunks = task.Chunks.Where(c => c.Index >= s.FirstChunk && c.Index < s.FirstChunk + EmbyConversionPlanner.ChunksPerSlice)
                    .OrderBy(c => c.Index)
                    .Select(c => new DiagnosticChunk(c.Index, c.Start, c.End, durations.TryGetValue(c.Index, out double d) ? d : null, c.Size))
                    .ToList();
                return new DiagnosticSlice(s.Index + 1, s.Start, s.End, s.Last, s.Done, s.Done ? s.DoneBy : null,
                    task.DiagnosticsLog.Runs.GetValueOrDefault(s.Index), task.DiagnosticsLog.Resumes.GetValueOrDefault(s.Index), chunks);
            }).ToList();
            var commands = DiagnosticCommands(task);
            // Last: nothing can fail after this, so a pin is always released (CollectDiagnosticsAsync).
            if (measure)
                spoolPins.Pin(task.Id);
            return new DiagnosticsRequest
            {
                Kind = kind,
                Error = error,
                Check = check,
                SourcePath = task.Input,
                SpoolId = task.Id,
                SpoolDir = measure ? SpoolDir(task.Id) : null,
                Slices = slices,
                Commands = commands,
                MachineFfmpeg = new Dictionary<string, string?>(task.DiagnosticsLog.MachineFfmpeg),
                ToneMapper = EmbyFilters.NeedsToneMapping(task.Requirements.Filters) ? ToneMapperName(task.Requirements.ToneMapping) : null,
                AudioState = task.Audio,
                AudioOffset = task.AudioOffset,
                SeekMargin = task.Slices.FirstOrDefault()?.SeekMargin,
            };
        }
    }

    /// <summary>The conversion's commands, as Emby's filters are translated for every machine; a run of slice 2 stands for all.</summary>
    private static List<DiagnosticCommand> DiagnosticCommands(EncodeTask task)
    {
        var commands = new List<DiagnosticCommand>();
        try
        {
            var slice = task.Slices.OrderBy(s => s.Index).Skip(1).FirstOrDefault() ?? task.Slices.FirstOrDefault();
            if (slice != null)
            {
                bool seek = slice.Index > 0;
                string[] run = EmbyFilters.TranslateArgs(EmbyConversionPlanner.ChunkArgs(task.ChunkTemplate, slice.FirstChunk, slice.Start, seek,
                    slice.Last ? null : slice.End, slice.End - slice.Start, slice.SeekMargin), task.Requirements.ToneMapping);
                commands.Add(new DiagnosticCommand($"Run of slice {slice.Index + 1} (worker and server; the server lists chunks as CSV)", run));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            commands.Add(new DiagnosticCommand("Chunk template", task.ChunkTemplate));
        }
        if (task.DiagnosticsLog.AssemblyTemplate is { } assembly)
            commands.Add(new DiagnosticCommand("Assembly by the shim", assembly));
        if (task.AudioAhead is { } audio)
        {
            commands.Add(new DiagnosticCommand("Audio converted ahead", audio.PrepareTemplate));
            commands.Add(new DiagnosticCommand("Assembly with that audio", audio.AssemblyTemplate));
        }
        return commands;
    }

    /// <summary>Runs the analysis and releases the spool. Never throws.</summary>
    private async Task<DiagnosticsResult?> CollectDiagnosticsAsync(EncodeTask task, DiagnosticsRequest request)
    {
        try
        {
            var result = await failureDiagnostics!.CollectAsync(request, diagnosticsStop.Token);
            lock (gate)
                return DiagnosticsAnalysis.WithFileStarts(result, task.DiagnosticsLog.FileStarts, request) with { FailingRuns = [.. task.DiagnosticsLog.FailingRuns] };
        }
        catch (Exception ex)
        {
            logger.LogDebug("Conversion {Id}: no diagnostics for the failure report ({Error})", task.Id, ex.GetType().Name);
            return null;
        }
        finally
        {
            if (spoolPins.Unpin(request.SpoolId))
                DeleteSpool(request.SpoolId);
        }
    }

    /// <summary>Notes a failed run for the report: it may be sent minutes later, once the analysis is over.</summary>
    private void NoteFailingRun(EncodeTask task, string error, string? machine, int? slice)
    {
        var lines = FailureReportFormatter.ErrorLines(error);
        if (lines.Count == 0)
            return;
        lock (gate)
            if (task.DiagnosticsLog.FailingRuns.Count < 10)
                task.DiagnosticsLog.FailingRuns.Add(new FailingRun(machine, slice, lines));
    }
}
