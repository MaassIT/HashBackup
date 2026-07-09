// Konfiguriere den Logger vorläufig mit Info-Level
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    // Erstellen und Ausführen der Anwendung
    var app = new Application(args);
    await app.RunAsync();
}
catch
{
    // Application.RunAsync already writes the exception once with full context.
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
