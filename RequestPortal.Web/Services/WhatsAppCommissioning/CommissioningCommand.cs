using System.Text.Json;
using Common.Messaging.Hosting;
using Common.Secrets;
using RequestPortal.Infrastructure.Messaging;

namespace RequestPortal.Web.Services.WhatsAppCommissioning;

public static class CommissioningCommand
{
    // Read-only inspection only. No send command, application host, migrations or workers.
    public static async Task<int> InspectAsync(WebApplicationBuilder builder)
    {
        try
        {
            if (!builder.Environment.IsProduction() || builder.Configuration["CommonSecrets:Mode"] != "Production")
                throw new InvalidOperationException();
            var options = builder.Configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>() ?? new();
            CommissioningGateway.RequireTestConfiguration(options);
            var overrides = new Dictionary<string,string?> { ["CommonSecrets:OpenBao:DeleteSecretIdFileAfterRead"] = "false" };
            foreach (var definition in builder.Configuration.GetSection("CommonSecrets:Providers").GetChildren())
                overrides[$"CommonSecrets:Providers:{definition.Key}:Settings:DeleteSecretIdFileAfterRead"] = "false";
            builder.Configuration.AddInMemoryCollection(overrides);
            await using var runtime = CommonSecretsBootstrapRuntime.Create(builder.Configuration);
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var gateway = new CommissioningGateway(client, new CommonSecretsChannelSecretResolver(runtime.Provider));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var readiness = await gateway.InspectAsync(options, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { status = readiness.Ready ? "ready-no-send" : "blocked-no-send",
                generalOutboundEnabled = false, readiness }));
            return readiness.Ready ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("{\"status\":\"blocked-no-send\",\"phase\":\"protected-configuration\",\"rawOutputWithheld\":true}");
            return 1;
        }
    }
}
