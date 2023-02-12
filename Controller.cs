using System.IO.Compression;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Extensions.Options;

namespace DotNet.Monitor.PProf.Api;

public class ControllerOptions
{
    public static readonly string Section = "Controller";

    public Uri? DotnetMonitorUrl { get; set; } = null;
}

[ApiController]
[Route("debug/pprof")]
public class Controller : ControllerBase
{
    private readonly ILogger<Controller> _logger;
    private readonly HttpClient _httpClient;
    private readonly ControllerOptions _options;

    public Controller(ILogger<Controller> logger, HttpClient httpClient, IOptionsSnapshot<ControllerOptions> options)
    {
        _logger = logger;
        _httpClient = httpClient;

        _options = options.Value;
    }

    [Route("profile")]
    [HttpGet]
    public async Task<IActionResult> Get(long seconds = 30, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(_options.DotnetMonitorUrl);

        var queryString = HttpUtility.ParseQueryString(_options.DotnetMonitorUrl.Query);
        queryString.Add("durationSeconds", seconds.ToString());
        queryString.Add("profile", "Cpu");

        var builder = new UriBuilder(_options.DotnetMonitorUrl);
        builder.Query = queryString.ToString();
        builder.Path = "/trace";

        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            _logger.LogDebug("Using temporary directory: {Path}", tempDir.FullName);

            var netTracePath = Path.Join(tempDir.FullName, "profile.nettrace");
            using (var responseStream = await _httpClient.GetStreamAsync(builder.Uri, token))
            {
                _logger.LogInformation("Response received, streaming to {Path}", netTracePath);
                using (var fileStream = System.IO.File.Create(netTracePath))
                {
                    await responseStream.CopyToAsync(fileStream, token);
                }
            }

            token.ThrowIfCancellationRequested();

            var etlxPath = Path.ChangeExtension(netTracePath, ".etlx");
            _logger.LogInformation("Converting .nettrace to .etlx at {Path}", etlxPath);

            TraceLog.CreateFromEventPipeDataFile(netTracePath, etlxPath, new TraceLogOptions { ContinueOnError = true });

            token.ThrowIfCancellationRequested();
            _logger.LogInformation("Converting .etlx to pprof");

            Converter converter;
            using (var symbolReader = new SymbolReader(TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath })
            using (var eventLog = new TraceLog(etlxPath))
            {
                var stackSource = new MutableTraceEventStackSource(eventLog)
                {
                    OnlyManagedCodeStacks = true
                };

                var computer = new SampleProfilerThreadTimeComputer(eventLog, symbolReader)
                {
                    IncludeEventSourceEvents = false
                };
                computer.GenerateThreadTimeStacks(stackSource);

                token.ThrowIfCancellationRequested();
                converter = new Converter(stackSource);
            }

            token.ThrowIfCancellationRequested();
            _logger.LogInformation("Compressing and returning response");

            var memoryStream = new MemoryStream();
            using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal, true))
            {
                converter.Serialize(gzipStream);
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            return new FileStreamResult(memoryStream, "application/octet-stream");
        }
        finally
        {
            Directory.Delete(tempDir.FullName, true);
        }
    }
}
