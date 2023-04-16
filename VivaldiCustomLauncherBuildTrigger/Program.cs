using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.XPath;
using McMaster.Extensions.CommandLineUtils;

namespace VivaldiCustomLauncherBuildTrigger;

public class Program {

    private static readonly AssemblyName        ASSEMBLY            = Assembly.GetExecutingAssembly().GetName();
    private static readonly XmlNamespaceManager NAMESPACES          = new(new NameTable());
    private static readonly Uri                 WORKFLOW_BASE_URI   = new("https://api.github.com/repos/Aldaviva/VivaldiCustomLauncher/actions/");
    private static readonly Uri                 TEST_DATA_DIRECTORY = new("https://raw.githubusercontent.com/Aldaviva/VivaldiCustomLauncher/master/Tests/Data/");

    static Program() {
        NAMESPACES.AddNamespace("sparkle", "http://www.andymatuschak.org/xml-namespaces/sparkle");
    }

    private readonly HttpClient httpClient = new(new SocketsHttpHandler {
        MaxConnectionsPerServer = 16,
        SslOptions              = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
        Proxy                   = new WebProxy("localhost", 9998),
        UseProxy                = false
    }) {
        DefaultRequestHeaders = {
            UserAgent = {
                new ProductInfoHeaderValue(new ProductHeaderValue(ASSEMBLY.Name!, ASSEMBLY.Version!.ToString(3))),
                new ProductInfoHeaderValue("(+mailto:ben@aldaviva.com)")
            }
        }
    };

    private readonly string? gitHubAccessToken;
    private readonly bool    isDryRun;

    public static async Task<int> Main(string[] args) {
        CommandLineApplication argumentParser = new();
        CommandOption<string> gitHubAccessTokenOption =
            argumentParser.Option<string>("--github-access-token", "Token with repo scope access to Aldaviva/VivaldiCustomLauncher", CommandOptionType.SingleValue);
        CommandOption<bool> dryRunOption = argumentParser.Option<bool>("-n|--dry-run", "Don't actually start any builds", CommandOptionType.NoValue);
        argumentParser.Parse(args);
        if (gitHubAccessTokenOption.Value() is { } gitHubAccessToken) {
            await new Program(gitHubAccessToken, dryRunOption.ParsedValue).buildIfOutdated();
            return 0;
        } else {
            Console.WriteLine($"Usage: {Process.GetCurrentProcess().ProcessName} --github-access-token XXXXXXXXX");
            return 1;
        }
    }

    public Program(string? gitHubAccessToken, bool isDryRun) {
        this.gitHubAccessToken = gitHubAccessToken;
        this.isDryRun          = isDryRun;
    }

    private async Task buildIfOutdated() {
        foreach (BuildType buildType in Enum.GetValues<BuildType>()) {
            bool wasBuildTriggered = await buildIfOutdated(buildType);
            if (wasBuildTriggered) {
                break;
            }
        }
    }

    /// <returns><c>true</c> if a build was triggered, or <c>false</c> if it was not</returns>
    private async Task<bool> buildIfOutdated(BuildType buildType) {
        // don't immediately await method calls because these two requests should run in parallel
        Task<string> latestVivaldiVersionTask = getLatestVivaldiVersionSparkle(buildType);
        Task<string> testedVivaldiVersionTask = getTestedVivaldiVersion(buildType);

        string latestVivaldiVersion = await latestVivaldiVersionTask;
        string testedVivaldiVersion = await testedVivaldiVersionTask;

        if (latestVivaldiVersion != testedVivaldiVersion && !await isBuildRunning()) {
            await triggerBuild(buildType);
            return true;
        } else {
            Console.WriteLine($"{buildType} is up-to-date, not triggering {buildType} build.");
            return false;
        }
    }

    private async Task<string> getTestedVivaldiVersion(BuildType buildType) {
        string version = (await httpClient.GetStringAsync(new Uri(TEST_DATA_DIRECTORY, $"vivaldi-{buildType.ToString().ToLower()}-version.txt"))).Trim();
        Console.WriteLine($"Tested Vivaldi {buildType} version: {version}");
        return version;
    }

    // about 550ms without an existing connection, about 180ms with keep-alive
    private async Task<string> getLatestVivaldiVersionSparkle(BuildType buildType) {
        Uri sparkleUrl = buildType switch {
            BuildType.SNAPSHOT => new Uri("https://update.vivaldi.com/update/1.0/win/appcast.x64.xml"),
            _                  => new Uri("https://update.vivaldi.com/update/1.0/public/appcast.x64.xml")
        };
        await using Stream stream = await httpClient.GetStreamAsync(sparkleUrl);
        XPathNavigator     xpath  = new XPathDocument(stream).CreateNavigator();

        string version = xpath.SelectSingleNode("/rss/channel/item/enclosure/@sparkle:version", NAMESPACES)!.Value;
        Console.WriteLine($"Latest Vivaldi {buildType} version: {version}");
        return version;
    }

    private async Task<bool> isBuildRunning() {
        using HttpResponseMessage response       = await sendGitHubApiRequest(new HttpRequestMessage(HttpMethod.Get, new Uri(WORKFLOW_BASE_URI, "runs?per_page=10")));
        await using Stream        responseStream = await response.Content.ReadAsStreamAsync();
        JsonDocument              responseJson   = await JsonDocument.ParseAsync(responseStream);
        JsonElement               runs           = responseJson.RootElement.GetProperty("workflow_runs");

        return runs.EnumerateArray().Any(run => {
            string latestBuildStatusRaw = run.GetProperty("status").GetString()!;
            Enum.TryParse(latestBuildStatusRaw, true, out WorkflowStatus latestBuildStatus);
            return latestBuildStatus is WorkflowStatus.IN_PROGRESS or WorkflowStatus.QUEUED or WorkflowStatus.REQUESTED or WorkflowStatus.WAITING;
        });
    }

    private async Task triggerBuild(BuildType buildType) {
        JsonObject requestBody = new() {
            {
                "ref", JsonValue.Create("master")
            }, {
                "inputs", new JsonObject {
                    { "buildType", JsonValue.Create(buildType.ToString().ToLower()) }
                }
            }
        };

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(WORKFLOW_BASE_URI, "workflows/build.yml/dispatches")) {
            Content = new StringContent(requestBody.ToJsonString(), new UTF8Encoding(false, true), new MediaTypeHeaderValue("application/json", "utf-8"))
        };

        if (!isDryRun) {
            using HttpResponseMessage response = await sendGitHubApiRequest(request);
        }

        Console.WriteLine("Build triggered.");
    }

    public async Task<HttpResponseMessage> sendGitHubApiRequest(HttpRequestMessage request, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead,
                                                                CancellationToken  cancellationToken = default) {
        if (gitHubAccessToken != null) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubAccessToken);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await httpClient.SendAsync(request, httpCompletionOption, cancellationToken);
    }

}