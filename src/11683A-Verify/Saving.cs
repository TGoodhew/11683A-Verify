using Spectre.Console;

namespace Verify11683A.App;

/// <summary>Saving a finished run so that a results folder that can't be written (locked, read-only, gone) doesn't lose it.</summary>
public static class Saving
{
    /// <summary>Where a run goes when the results folder can't be written.</summary>
    public static string FallbackDirectory => Path.Combine(Path.GetTempPath(), "11683A-Verify-results");

    /// <summary>Saves with <paramref name="save"/> into <paramref name="directory"/>, or, if that fails, into <see cref="FallbackDirectory"/>.</summary>
    public static string WithFallback(Func<string, string> save, string directory, SessionLog log)
    {
        try
        {
            return save(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Could not save in {Path.GetFullPath(directory)}: {ex.Message}; saving in {FallbackDirectory}");
            AnsiConsole.MarkupLine($"[yellow]Could not save in {Markup.Escape(Path.GetFullPath(directory))} ({Markup.Escape(ex.Message)}). "
                + $"Saving in {Markup.Escape(FallbackDirectory)} instead.[/]");
            return save(FallbackDirectory);
        }
    }

    /// <summary>The session log copied beside the results; a failure is reported (the log itself is still in logs\).</summary>
    public static void CopyLog(SessionLog log, string path)
    {
        try
        {
            log.SaveCopy(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[yellow]The session log could not be copied beside the results ({Markup.Escape(ex.Message)}); it is in {Markup.Escape(Path.GetFullPath(log.Path))}.[/]");
        }
    }
}
