using CommandLine;
using MassTransit;
using MassTransit.MySqlOutbox.Demo.Consumer.Contexts;
using MassTransit.MySqlOutbox.Demo.Shared.Extensions;
using MassTransit.MySqlOutbox.Extensions;
using Microsoft.EntityFrameworkCore;

var parserResult = Parser.Default.ParseArguments<Options>(args);

if (parserResult.Errors.Any())
{
   Console.WriteLine(parserResult.Errors.ToString());// Handle errors
   return;
}

var consumerId = parserResult.Value.ConsumerId;

var configuration = new ConfigurationBuilder()
   .AddJsonFile("appsettings.json")
   .AddEnvironmentVariables()
   .Build();

var connectionString = configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
   throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
}

var host = Host.CreateDefaultBuilder(args)
   .ConfigureLogging(logging =>
   {
      logging.AddConsole();
      logging.AddDebug();
      logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
   })
   .ConfigureServices((context, services) =>
   {
      services.AddSingleton(new Options() { ConsumerId = consumerId });
      services.AddMassTransit(x =>
      {
         x.AddConsumers(typeof(Program).Assembly);
         x.SetKebabCaseEndpointNameFormatter();
         x.UsingRabbitMq((context, cfg) =>
         {
            cfg.Host(configuration.GetConnectionString("RabbitMq"));
            cfg.ConfigureEndpoints(context);
            cfg.UseMessageRetry(r =>
            {
               r.Ignore<ApplicationException>();
               r.Exponential(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(2));
            });
         });
      });

      services.AddMySqlContext<ConsumerContext>(connectionString);
      services.AddOutboxInboxServices<ConsumerContext>();
      services.AddDbContext<CreationConsumerContext>(options =>
      {
         options.UseMySql(
            connectionString,
            ServerVersion.AutoDetect(connectionString),
            mySqlOptions => mySqlOptions.EnableRetryOnFailure(
                  maxRetryCount: 5,
                  maxRetryDelay: TimeSpan.FromSeconds(15),
                  errorNumbersToAdd: [1042, 1158, 1159, 1160, 1927] // a few transient MySQL errors
            ));
      });
   })
   .Build();


Console.WriteLine($"Consumer {consumerId} is running...");
Console.WriteLine("Waiting for messages");

await host.RunAsync();

public record Options
{
   [Option('i', "id", Required = false, HelpText = "Identifier for the publisher instance", Default = "1")]
   public string? ConsumerId { get; init; }
};