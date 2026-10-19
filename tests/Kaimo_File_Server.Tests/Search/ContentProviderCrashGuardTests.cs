using System.IO.Compression;
using Kaimo_File_Server.Search;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// A document whose parsing kills the process (out of memory, stack overflow) must not do so
/// again on every restart: a marker on persistent storage records the extraction in flight, and
/// content whose extraction died repeatedly is indexed by name only.
/// </summary>
public sealed class ContentProviderCrashGuardTests
{
    // Shared by all tests (the guard directory is process-wide); markers are content-addressed,
    // so tests using different bytes never see each other's markers.
    private static readonly string GuardDir = Path.Combine(Path.GetTempPath(), "kaimo-extract-guard-tests");

    private readonly ListLogger _log = new();

    public ContentProviderCrashGuardTests() => ContentProvider.GuardDirectory = GuardDir;

    [Fact]
    public void Extraction_IsMarkedWhileRunning_AndUnmarkedAfterwards()
    {
        byte[] bytes = UniqueBytes();
        string marker = ContentProvider.GuardMarkerPath(GuardDir, bytes);
        bool markedDuringExtraction = false;

        string text = ContentProvider.ExtractGuarded(b =>
        {
            markedDuringExtraction = File.Exists(marker);
            return "extracted";
        }, bytes, "a.pdf", _log);

        Assert.Equal("extracted", text);
        Assert.True(markedDuringExtraction);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void FailingExtraction_IsUnmarked()
    {
        byte[] bytes = UniqueBytes();

        string text = ContentProvider.ExtractGuarded(_ => throw new InvalidDataException("corrupt"), bytes, "a.pdf", _log);

        Assert.Equal(string.Empty, text);
        Assert.False(File.Exists(ContentProvider.GuardMarkerPath(GuardDir, bytes)));
    }

    [Fact]
    public void MarkerOfOneDeadAttempt_IsRetried_AndClearedOnSuccess()
    {
        // A process can die mid-extraction for reasons unrelated to the file (docker stop during
        // a reindex, an OOM caused elsewhere); one dead attempt must not quarantine the content.
        byte[] bytes = UniqueBytes();
        string marker = ContentProvider.GuardMarkerPath(GuardDir, bytes);
        ContentProvider.WriteGuardMarker(marker, attempts: 1, "a.pdf");
        int attemptsDuringRetry = 0;

        string text = ContentProvider.ExtractGuarded(_ =>
        {
            attemptsDuringRetry = ContentProvider.ReadGuardMarkerAttempts(marker);
            return "extracted";
        }, bytes, "a.pdf", _log);

        Assert.Equal("extracted", text);
        // Dying during the retry leaves a marker that quarantines the content on the next start.
        Assert.Equal(ContentProvider.MaxExtractionAttempts, attemptsDuringRetry);
        Assert.False(File.Exists(marker));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void MarkerOfRepeatedDeadAttempts_SkipsExtraction_AndLogsTheFile()
    {
        byte[] bytes = UniqueBytes();
        string marker = ContentProvider.GuardMarkerPath(GuardDir, bytes);
        ContentProvider.WriteGuardMarker(marker, ContentProvider.MaxExtractionAttempts, "a.pdf");
        try
        {
            string text = ContentProvider.ExtractGuarded(_ => throw new Exception("must not run"), bytes, "a.pdf", _log);

            Assert.Equal(string.Empty, text);
            Assert.Contains(_log.Warnings, w => w.Contains("a.pdf"));
            // The file stays quarantined until its content changes or an admin removes the marker.
            Assert.True(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void UnreadableMarker_CountsAsOneDeadAttempt()
    {
        byte[] bytes = UniqueBytes();
        string marker = ContentProvider.GuardMarkerPath(GuardDir, bytes);
        Directory.CreateDirectory(GuardDir);
        File.WriteAllText(marker, "garbage");

        Assert.Equal("ok", ContentProvider.ExtractGuarded(_ => "ok", bytes, "a.pdf", _log));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task GetContent_DocumentWithLeftoverMarker_IsIndexedByNameOnly()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open()))
            entry.Write($"<sst xmlns=\"x\"><si><t>poison {Guid.NewGuid()}</t></si></sst>");
        string marker = ContentProvider.GuardMarkerPath(GuardDir, stream.ToArray());
        ContentProvider.WriteGuardMarker(marker, ContentProvider.MaxExtractionAttempts, "book.xlsx");
        stream.Position = 0;
        try
        {
            Assert.Equal(string.Empty, await ContentProvider.GetContent(stream, "book.xlsx", _log));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void UnwritableGuardDirectory_StillExtracts()
    {
        // A file where the directory should be: markers cannot be created.
        string blocker = Path.Combine(Path.GetTempPath(), $"kaimo-guard-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            Assert.Equal("ok", ContentProvider.ExtractGuarded(_ => "ok", UniqueBytes(), "a.pdf", _log, guardDirectory: blocker));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private static byte[] UniqueBytes() => Guid.NewGuid().ToByteArray();

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
