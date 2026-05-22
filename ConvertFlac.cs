//  ConvertFlac
//  Cèlia Casanovas (Akirako Saint-Just)
//
//  Runs ffmpeg parallelly to convert FLACs to V0 VBR MP3s using LAME
//  I just tried to be as "modern" and "functional" as possible as a personal challenge,
//  so I used channels for parallelism and a functional, "Redux style", UI state pipeline,
//  as well as "new" C# constructs and patterns
//
//  the encoding task is a pure producer (aka writes to the channel),
//  whilst the dashboard UI is a pure consumer (aka reads the channel)
//
//  UI states are calculated in a "pure", functional way, then rendered
//
//  I did my best to keep side effects isolated at the beginning and end of any path
//
//  args[0] is the target folder
//  if none is specified, the user will be prompted for one
//
//  args[1] is the parallelism limit
//  if none is specified, it's set to the core count of the system divided by 1.5
//
//  TODO: extract all strings so that I can easily write a Japanese and a Catalan version
//  TODO: extract all text colours so that they can easily be changed or themed (?)
//      so far,
//      business as usual is    ConsoleColor.Cyan,
//      errors are              ConsoleColor.Red,
//      relevant messages are   ConsoleColor.Yellow,
//      success is              ConsoleColor.Green,
//      and prompts use the default colour
//  TODO: offer encoding options maybe? dunno
//  TODO: better validation!!!!!!!!!!
//

using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!IsToolAvailable("ffmpeg"))
{
    WriteColour("\n  ERROR: ffmpeg was not found on your PATH.", ConsoleColor.Red);
    WriteColour("  Install it with:  winget install Gyan.FFmpeg\n", ConsoleColor.Red);
    return;
}

string folderPath = args.Length > 0 ? args[0] : "";

// if args[1] is a valid integer, it's assigned to the maximum parallelism,
// unless it's < 1, in which case it's set to 1
// if it's not specified or isn't a valid integer, the maximum parallelism is
// the system's core count divided by 1.5
int maxConcurrent =
    args.Length > 1 && int.TryParse(args[1], out var mc)
        ? Math.Max(1, mc)
        : Math.Max(1, (int)(Environment.ProcessorCount / 1.5));

if (string.IsNullOrWhiteSpace(folderPath))
{
    WriteColour("\n  No folder path provided.", ConsoleColor.Cyan);
    Console.Write("  Enter the full path to your music folder: ");
    folderPath = Console.ReadLine() ?? string.Empty;
}

folderPath = folderPath.Trim('"', '\'');

if (!Directory.Exists(folderPath))
{
    WriteColour($"  ERROR: Folder not found: {folderPath}", ConsoleColor.Red);
    return;
}

// I wonder if I should offer the option not to make it recursive
string[] flacFiles = Directory.GetFiles(folderPath, "*.flac", SearchOption.AllDirectories);
if (flacFiles.Length == 0)
{
    WriteColour($"\n  No .flac files found in: {folderPath}", ConsoleColor.Yellow);
    return;
}

WriteColour(
    $"\n  Found {flacFiles.Length} .flac file(s). Encoding with up to {maxConcurrent} parallel jobs.",
    ConsoleColor.Cyan
);
Console.Write("\n  Delete original FLAC files after successful conversion? (y/N): ");
bool deleteFlacs =
    Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ?? false;

// if Ctrl+C is pressed, all running tasks are cancelled
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// should there be any limits just to stay safe? can't think of a bad scenario but who knows
var channel = Channel.CreateUnbounded<EncodeEvent>();

// encoding runs in a "thread pool" (ie Parallel.ForEachAsync),
// whilst the dashboard runs in the "main thread" (just an async method)
var encodeTask = RunEncoderPipelineAsync(
    flacFiles,
    maxConcurrent,
    deleteFlacs,
    channel.Writer,
    cts.Token
);
var dashboardTask = RunDashboardAsync(flacFiles.Length, channel.Reader, cts);

// just wait for the pipeline to resolve
await encodeTask;
await dashboardTask;

async Task RunEncoderPipelineAsync(
    string[] files,
    int parallelism,
    bool deleteFlacs,
    ChannelWriter<EncodeEvent> writer,
    CancellationToken ct
)
{
    var options = new ParallelOptions
    {
        MaxDegreeOfParallelism = parallelism,
        CancellationToken = ct
    };

    // parallel encoding
    await Parallel.ForEachAsync(
        files,
        options,
        async (flacPath, token) =>
        {
            var fileName = Path.GetFileName(flacPath);
            var mp3Path = Path.ChangeExtension(flacPath, ".mp3");

            // if an mp3 file corresponding to the current FLAC already exists,
            // check whether it's valid
            // if it is, skip the FLAC (finish the job as Skipped)
            if (File.Exists(mp3Path) && await ValidateMp3Async(mp3Path))
            {
                writer.TryWrite(new JobFinished(fileName, new(flacPath, Status.Skipped)));
                return;
            }

            // sending a message that the job has started,
            // so that the dashboard can know
            writer.TryWrite(new JobStarted(fileName));

            // if there was an mp3 but it wasn't valid,
            // it is deleted and conversion proceeds
            if (File.Exists(mp3Path))
                File.Delete(mp3Path);

            // actual conversion!
            var (ok, err) = await RunFfmpegAsync(
                $"-i \"{flacPath}\" -map_metadata 0 -id3v2_version 3 -codec:a libmp3lame -q:a 0 -y -v error \"{mp3Path}\""
            );

            // validate whether the converted mp3 is valid
            // if it is, the original FLAC is deleted
            // the way I've written this is kinda redundant, I wonder whether I should refactor it
            // how do we feel about out params btw?
            if (ok && File.Exists(mp3Path) && await ValidateMp3Async(mp3Path))
            {
                // if deleting the flac fails, the job is marked as DeleteFailed
                if (deleteFlacs && !TryDeleteFile(flacPath, out string delErr))
                {
                    writer.TryWrite(
                        new JobFinished(fileName, new(flacPath, Status.DeleteFailed, delErr))
                    );
                    return;
                }
                writer.TryWrite(new JobFinished(fileName, new(flacPath, Status.Success)));
                return;
            }

            // if validation of the converted mp3 fails or RunFfmpegAsync returns an error,
            // the invalid mp3 is deleted and the job is finished as Failed
            if (File.Exists(mp3Path))
                File.Delete(mp3Path);
            writer.TryWrite(new JobFinished(fileName, new(flacPath, Status.Failed, err)));
        }
    );

    writer.Complete();
}

async Task RunDashboardAsync(
    int totalFiles,
    ChannelReader<EncodeEvent> reader,
    CancellationTokenSource cts
)
{
    // rerender time: 400ms
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
    var state = DashboardState.Empty;
    int tick = 0;
    int drawnLines = 0;

    // no CancellationToken, so that any pending messages can be printed on termination
    // instead of the UI just ending abruptly
    while (await timer.WaitForNextTickAsync())
    {
        // read any messages coming from the encoder pipeline
        // and "reduce" them into a UI state object
        while (reader.TryRead(out var ev))
        {
            state = state.Apply(ev);
        }

        // calculate the next frame from the state object
        var frame = BuildFrame(state, totalFiles, tick++, cts.Token.IsCancellationRequested);

        // this is where the side-effects happen!
        //
        // first, the previous frame is cleared from the screen
        // so that the next frame can be rendered
        // the way i've done this is with that weird string literal in the Console.Write
        // it turns out there are character sequences which tell the terminal to
        // expect a command that lets you move the cursor around, clear parts of the screen, etc
        //
        // \x1b is the ANSI escape character, which tells the terminal to expect a command sequence
        //
        // [ opens the command sequence
        //
        // A is an ANSI command that lets you move the cursor up,
        // so{drawnLines}A moves the cursor up drawnLines times
        //
        // the second \x1b and the second [ open a second ANSI command sequence
        //
        // J is an ANSI command that deletes everything from the cursor position
        // to the end of the screen
        //
        if (drawnLines > 0)
            Console.Write($"\x1b[{drawnLines}A\x1b[J");
        foreach (var line in frame.Lines)
            WriteColour(line, ConsoleColor.Cyan);
        drawnLines = frame.Lines.Count;

        // if there are no messages left to read and no pending jobs,
        // the rendering loop ends
        if (reader.Completion.IsCompleted && state.ActiveJobs.Count == 0)
            break;
    }

    DrawFinalSummary(state.Results);
}

void DrawFinalSummary(ImmutableList<ConversionResult> results)
{
    var skipped = results.Where(r => r.State == Status.Skipped).ToList();
    var failures = results.Where(r => r.State is Status.Failed or Status.DeleteFailed).ToList();

    // the dashboard job list is cleared off the screen
    // check the comments inside RunDashboardAsync for an explanation of
    // the weird string literal with ANSI commands
    Console.Write("\x1b[J\n");

    if (skipped.Count > 0)
    {
        WriteColour("  Skipped files (MP3 already existed):", ConsoleColor.Yellow);
        foreach (var f in skipped)
            WriteColour($"    {f.FilePath}", ConsoleColor.Yellow);
    }

    if (failures.Count > 0)
    {
        WriteColour("\n  Failed files:", ConsoleColor.Red);
        foreach (var f in failures)
        {
            WriteColour($"    [{f.State.ToString().ToUpper()}] {f.FilePath}", ConsoleColor.Red);
            if (!string.IsNullOrEmpty(f.Error))
                WriteColour($"      {f.Error.Replace("\n", "\n      ")}", ConsoleColor.Red);
        }
    }

    WriteColour("\n  -------------------------------------------", ConsoleColor.Cyan);
    WriteColour(
        $"  Converted : {results.Count(x => x.State == Status.Success)}",
        ConsoleColor.Green
    );
    WriteColour($"  Skipped   : {skipped.Count}", ConsoleColor.Yellow);
    WriteColour(
        $"  Failed    : {failures.Count(x => x.State == Status.Failed)}",
        failures.Any(x => x.State == Status.Failed) ? ConsoleColor.Red : ConsoleColor.Gray
    );
    WriteColour(
        $"  Del. fail : {failures.Count(x => x.State == Status.DeleteFailed)}",
        failures.Any(x => x.State == Status.DeleteFailed) ? ConsoleColor.Red : ConsoleColor.Gray
    );
    WriteColour("  -------------------------------------------\n", ConsoleColor.Cyan);
}

async Task<(bool Ok, string Error)> RunFfmpegAsync(string arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode == 0, error);
}

// validation is performed by letting ffmpeg run through the file
// it fails if there's non-mp3 data, but it doesn't work for abruptly truncated
// files because mp3s just end with no EOF marker or anything, which is kinda concerning imo
async Task<bool> ValidateMp3Async(string path) =>
    (await RunFfmpegAsync($"-v error -i \"{path}\" -f null -")).Ok;

bool IsToolAvailable(string tool)
{
    try
    {
        using var p = Process.Start(
            new ProcessStartInfo
            {
                FileName = tool,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        );
        p?.WaitForExit(1000);
        return p?.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

bool TryDeleteFile(string path, out string error)
{
    try
    {
        File.Delete(path);
        error = string.Empty;
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

void WriteColour(string msg, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(msg);
    Console.ResetColor();
}

RenderedFrame BuildFrame(DashboardState state, int totalFiles, int tick, bool isCancelling)
{
    // this array contains the states of the spinner in sequence
    char[] spinChars = ['\u280B', '\u2819', '\u2839', '\u2838', '\u283C', '\u2834', '\u2826', '\u2827', '\u2807', '\u280F'];

    int w = ConsoleUtils.GetConsoleWidth();
    // progress bar width is min 10 chars wide, max 32 chars wide
    int barW = Math.Clamp(w - 24, 10, 32);
    int nDone = state.Success + state.Skipped + state.Failed;
    // calculating how many jobs are over so as to fill the progress bar accordingly
    int filled = (int)Math.Floor(nDone / (double)totalFiles * barW);
    // \u2588 is the "filled tofu" character,
    // \u2591 is the "empty tofu"
    string bar = new string('\u2588', filled) + new string('\u2591', barW - filled);

    // the spinner state is picked from the spinChars array depending on the current tick of the timer
    var lines = new List<string>
    {
        "",
        $"  {spinChars[tick % spinChars.Length]}  [{bar}]  {nDone} / {totalFiles} files"
            + (isCancelling ? "  [CANCELLING...]" : ""),
        ""
    };

    // i'm taking 5 to avoid a huge list but tbh it doesn't matter does it
    // file names are capped at the console width minus 15 chars
    foreach (var job in state.ActiveJobs.Take(5))
        lines.Add($"     \u27F3  {job.Truncate(w - 15)}");

    if (state.ActiveJobs.Count > 5)
        lines.Add($"     ... and {state.ActiveJobs.Count - 5} more");
    else if (state.ActiveJobs.Count == 0 && nDone < totalFiles && !isCancelling)
        lines.Add("     (starting jobs...)");
    else if (state.ActiveJobs.Count == 0 && nDone == totalFiles)
        lines.Add("     (finished)");

    lines.Add("");
    lines.Add(
        $"  \u2713 {state.Success} converted   \u2298 {state.Skipped} skipped   \u2717 {state.Failed} failed"
    );
    lines.Add("");

    return new RenderedFrame(lines);
}

static class ConsoleUtils
{
    // extension methods!!
    // actually an extension method that's a super terse lambda!!
    // with a substring calculated using indices!
    // i'm so cool, smooth even, sunglasses emoji
    public static string Truncate(this string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..(maxChars - 1)] + "…";

    public static int GetConsoleWidth()
    {
        if (Console.IsOutputRedirected)
            return 80;
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }
}

enum Status
{
    Skipped,
    Success,
    Failed,
    DeleteFailed
}

readonly record struct ConversionResult(string FilePath, Status State, string Error = "");

readonly record struct RenderedFrame(List<string> Lines);

// discriminated union innit!!
abstract record EncodeEvent;

record JobStarted(string FileName) : EncodeEvent;

record JobFinished(string FileName, ConversionResult Result) : EncodeEvent;

// "redux style" immutable state lol
record DashboardState(
    ImmutableList<string> ActiveJobs,
    ImmutableList<ConversionResult> Results,
    int Success,
    int Skipped,
    int Failed
)
{
    // hey hey it's collection expressions!
    public static DashboardState Empty => new([], [], 0, 0, 0);

    public DashboardState Apply(EncodeEvent ev) =>
        ev switch
        {
            JobStarted s => this with { ActiveJobs = ActiveJobs.Add(s.FileName) },
            JobFinished f
                => this with
                {
                    ActiveJobs = ActiveJobs.Remove(f.FileName),
                    Results = Results.Add(f.Result),
                    Success = Success + (f.Result.State == Status.Success ? 1 : 0),
                    Skipped = Skipped + (f.Result.State == Status.Skipped ? 1 : 0),
                    Failed =
                        Failed + (f.Result.State is Status.Failed or Status.DeleteFailed ? 1 : 0)
                },
            _ => this
        };
}
