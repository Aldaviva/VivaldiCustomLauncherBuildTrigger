using System.Text;
using System.Text.Json;

VivaldiCustomLauncherBuildTrigger.Program program = new(null);

var runDurations = new List<(int startedMinutesOfHour, int runDurationSeconds)>();

int  page       = 1;
int? totalCount = null;
do {
    Uri requestUri = new("https://api.github.com/repos/Aldaviva/VivaldiCustomLauncher/actions/workflows/build.yml/runs?status=success&per_page=100&page={page}");
    Console.WriteLine($"Fetching page {page:N0}");
    page++;

    using HttpResponseMessage response          = await program.sendGitHubApiRequest(new HttpRequestMessage(HttpMethod.Get, requestUri));
    await using Stream        readAsStreamAsync = await response.Content.ReadAsStreamAsync();
    JsonDocument              responseDoc       = await JsonDocument.ParseAsync(readAsStreamAsync);

    totalCount ??= responseDoc.RootElement.GetProperty("total_count").GetInt32();

    runDurations.AddRange(responseDoc.RootElement.GetProperty("workflow_runs").EnumerateArray().Select(run => {
        DateTimeOffset started             = run.GetProperty("run_started_at").GetDateTimeOffset();
        DateTimeOffset completed           = run.GetProperty("updated_at").GetDateTimeOffset();
        int            startedMinuteOfHour = started.Minute;
        int            runDurationSeconds  = (int) (completed - started).TotalSeconds;
        return (startedMinuteOfHour, runDurationSeconds);
    }));

    Console.WriteLine("done");
} while (runDurations.Count < totalCount);

StringBuilder outputBuilder = new("startedMinutesOfHour\trunDurationSeconds\r\n");
foreach ((int startedMinutesOfHour, int runDurationSeconds) in runDurations) {
    outputBuilder.AppendLine($"{startedMinutesOfHour}\t{runDurationSeconds}");
}

string output = outputBuilder.ToString();
await File.WriteAllTextAsync("output.txt", output, new UTF8Encoding(false, true));
Console.WriteLine(output);